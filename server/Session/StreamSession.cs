using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using DeskStreamer.Server.Audio;
using DeskStreamer.Server.Capture;
using DeskStreamer.Server.Encode;
using DeskStreamer.Server.Input;
using DeskStreamer.Server.Net;
using DeskStreamer.Server.Protocol;
using Vortice.Direct3D11;

namespace DeskStreamer.Server.Session;

public enum SessionState
{
    AwaitingHello,
    AwaitingPairRequest,
    AwaitingPairCode,
    Ready,
    Streaming,
}

/// <summary>
/// Per-connection session controller: implements the control-channel state machine
/// (PROTOCOL.md §2), owns the capture/encode/send pipeline, and runs the adaptation
/// controller (§4). All control messages are delivered from ControlServer's single read
/// loop, so message handling here is single-threaded (the encoded-frame counters, touched
/// from the pipeline threads, use Interlocked / volatile).
/// </summary>
public sealed class StreamSession : IDisposable
{
    private readonly Action<object> _send;
    private readonly Action _requestClose;
    private readonly PairingManager _pairing;
    private readonly string _serverName;
    private readonly IPAddress? _clientAddress;
    private readonly ServerOptions _options;

    // Dashboard commands are enqueued from dashboard connection threads and drained on the control
    // read loop (see DrainCommands / ControlServer) so session start/stop stays single-threaded.
    private readonly ConcurrentQueue<Action> _commands = new();

    private SessionState _state = SessionState.AwaitingHello;
    private string _clientId = "";
    private string _clientName = "";

    // Pairing
    private string? _pin;
    private int _pinAttempts;
    private long _pinExpiryMs;

    // Display
    private int _displayWidth = 1920;
    private int _displayHeight = 1080;
    private bool _displayResolved;

    // Pipeline
    private MediaSender? _sender;
    private AudioSender? _audioSender;
    private SystemAudioCapture? _audioCapture;
    private VirtualGamepadManager? _gamepads;
    private RemoteMouseManager? _mouse;
    private RemoteKeyboardManager? _keyboard;
    private DesktopDuplicator? _duplicator;
    private Nv12Converter? _converter;
    private IVideoEncoder? _encoder;
    private Thread? _captureThread;
    private CancellationTokenSource? _captureCts;
    private ID3D11Texture2D? _lastNv12;
    private int _endpointIdrPending;
    private Stopwatch? _clock;
    private long _clockBaseUs;
    private uint _frameId;
    private volatile bool _streaming;
    private volatile bool _audioStreaming;

    // Quality / authoritative streamed dimensions (PROTOCOL.md §2.3).
    private string _quality = "native";
    private int _streamWidth;
    private int _streamHeight;

    // Adaptation
    private int _maxBitrateKbps = ServerOptions.DefaultMaxBitrateKbps;
    private int _currentBitrateKbps;
    private int _cleanStreak;
    private int _idrSinceLastStats;
    private readonly Queue<long> _idrTimes = new();
    private long _lastIdrGenMs = -1000;
    private int _bestCaptureToReceiveMs = int.MaxValue;
    private int _bestDecodeToSurfaceMs = int.MaxValue;

    // Stats surfaced to the console (Program reads these)
    private long _encodedFrames;
    private int _capturedSinceEncode;
    private long _idrRequestTotal;
    private int _pipelineFaultHandling;
    public SessionState State => _state;
    public bool Streaming => _streaming;
    public bool AudioStreaming => _audioStreaming;
    public int GamepadCount => _gamepads?.Count ?? 0;
    public int Fps { get; private set; } = 60;
    public int CurrentBitrateKbps => _currentBitrateKbps;
    public int LastClientFramesDropped { get; private set; }

    // Surfaced to the web dashboard.
    public string ClientName => _clientName;
    public string? ClientIp => _clientAddress?.ToString();
    public string Quality => _quality;
    public int StreamWidth => _streamWidth;
    public int StreamHeight => _streamHeight;
    public string EncoderBackend => _encoder?.BackendName ?? "";

    /// <summary>The pending pairing PIN while awaiting a PAIR_CODE, otherwise null.</summary>
    public string? PendingPin => _state == SessionState.AwaitingPairCode ? _pin : null;
    public long EncodedFrames => Interlocked.Read(ref _encodedFrames);
    public long IdrRequestTotal => Interlocked.Read(ref _idrRequestTotal);
    public long AudioPacketsSent => _audioSender?.PacketsSent ?? 0;
    public long AudioBytesSent => _audioSender?.BytesSent ?? 0;
    public bool MediaEndpointReady => _sender?.HasClient == true;
    public long MediaFramesSent => _sender?.FramesSent ?? 0;
    public long MediaBytesSent => _sender?.BytesSent ?? 0;

    public StreamSession(
        Action<object> send,
        Action requestClose,
        PairingManager pairing,
        string serverName,
        IPAddress? clientAddress,
        ServerOptions options)
    {
        _send = send;
        _requestClose = requestClose;
        _pairing = pairing;
        _serverName = serverName;
        _clientAddress = clientAddress;
        _options = options;
    }

    private static long NowMs() => MonotonicClock.NowMs;

    private static long NowUs() => MonotonicClock.NowUs;

    // ---- Message dispatch -----------------------------------------------------------------

    public void HandleMessage(string type, ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case "HELLO": OnHello(payload); break;
            case "PAIR_REQUEST": OnPairRequest(); break;
            case "PAIR_CODE": OnPairCode(payload); break;
            case "START_STREAM": OnStartStream(payload); break;
            case "MEDIA_READY": OnMediaReady(payload); break;
            case "AUDIO_START": OnAudioStart(); break;
            case "AUDIO_READY": OnAudioReady(payload); break;
            case "GAMEPAD_START": OnGamepadStart(payload); break;
            case "GAMEPAD_STOP": StopGamepads(); break;
            case "INPUT_START": OnInputStart(payload); break;
            case "MOUSE_BUTTON": OnMouseButton(payload); break;
            case "MOUSE_RESET": _mouse?.Reset(); break;
            case "KEYBOARD_KEY": OnKeyboardKey(payload); break;
            case "KEYBOARD_RESET": _keyboard?.Reset(); break;
            case "INPUT_STOP": StopInput(); break;
            case "STOP_STREAM": OnStopStream(); break;
            case "REQUEST_IDR": OnRequestIdr(); break;
            case "STATS": OnStats(payload); break;
            default: /* Unknown types MUST be ignored (PROTOCOL.md §2). */ break;
        }
    }

    private void OnHello(ReadOnlySpan<byte> payload)
    {
        var hello = Json.Deserialize<HelloMessage>(payload);
        if (hello == null)
        {
            _send(OutgoingMessages.Error("BAD_REQUEST", "malformed HELLO"));
            _requestClose();
            return;
        }

        if (hello.Ver != ProtocolConstants.Version)
        {
            _send(OutgoingMessages.Error("BAD_VERSION", $"server speaks protocol v{ProtocolConstants.Version}"));
            _requestClose();
            return;
        }

        _clientId = hello.ClientId;
        _clientName = hello.ClientName;
        ResolveDisplay();

        if (!string.IsNullOrEmpty(hello.Token) && _pairing.Verify(hello.ClientId, hello.Token))
        {
            _send(OutgoingMessages.HelloOk(_serverName, _displayWidth, _displayHeight));
            _state = SessionState.Ready;
            Console.WriteLine($"[session] client '{hello.ClientName}' connected and authenticated.");
        }
        else
        {
            _send(OutgoingMessages.PairRequired());
            _state = SessionState.AwaitingPairRequest;
        }
    }

    private void OnPairRequest()
    {
        if (_state != SessionState.AwaitingPairRequest)
            return;

        _pin = PairingManager.GeneratePin();
        _pinAttempts = 3;
        _pinExpiryMs = NowMs() + 60_000;
        _state = SessionState.AwaitingPairCode;
        PrintPin(_pin);
    }

    private void OnPairCode(ReadOnlySpan<byte> payload)
    {
        if (_state != SessionState.AwaitingPairCode || _pin == null)
            return;

        if (NowMs() > _pinExpiryMs)
        {
            _send(OutgoingMessages.Error("PAIR_TIMEOUT", "pairing window expired"));
            _requestClose();
            return;
        }

        var msg = Json.Deserialize<PairCodeMessage>(payload);
        if (msg != null && msg.Pin == _pin)
        {
            var token = _pairing.Pair(_clientId);
            _send(OutgoingMessages.PairOk(token));
            _pin = null;
            _state = SessionState.AwaitingHello; // client re-sends HELLO with the token
            Console.WriteLine("[session] pairing succeeded; token issued.");
        }
        else
        {
            _pinAttempts--;
            if (_pinAttempts > 0)
            {
                _send(OutgoingMessages.PairFail(_pinAttempts));
            }
            else
            {
                _send(OutgoingMessages.Error("PAIR_FAILED", "too many incorrect PINs"));
                _requestClose();
            }
        }
    }

    private void OnStartStream(ReadOnlySpan<byte> payload)
    {
        // Idempotent restart: the client may re-send START_STREAM (e.g. returning to the
        // foreground) without waiting for STREAM_STOPPED (PROTOCOL.md §5).
        if (_state == SessionState.Streaming)
        {
            StopPipeline();
            _state = SessionState.Ready;
        }

        if (_state != SessionState.Ready)
            return;

        var msg = Json.Deserialize<StartStreamMessage>(payload);
        _maxBitrateKbps = _options.ClampClientBitrateKbps(msg?.MaxBitrateKbps ?? 0);
        Fps = msg is { Fps: >= 15 and <= 240 } ? msg.Fps : 60;
        // Quality is fixed per stream: honor an explicit "720p"/"native", but fall back to the
        // server-wide default when the client sends no quality field (old v0.4.0 clients).
        _quality = ResolveRequestedQuality(msg?.Quality);

        BeginStream();
    }

    /// <summary>
    /// Starts the pipeline for the already-selected parameters and reports STREAM_STARTED. Shared
    /// by client START_STREAM and the dashboard Restart command so both paths behave identically.
    /// </summary>
    private void BeginStream()
    {
        try
        {
            StartPipeline();
            _send(OutgoingMessages.StreamStarted(
                _sender!.Port,
                _streamWidth,
                _streamHeight,
                Fps,
                _encoder!.BackendName,
                _clockBaseUs));
            _state = SessionState.Streaming;
            Console.WriteLine($"[session] streaming started: {_streamWidth}x{_streamHeight}@{Fps} " +
                              $"({_quality}), start bitrate {_currentBitrateKbps} kbps, media port {_sender.Port}.");
        }
        catch (EncoderUnavailableException ex)
        {
            // A capability failure belongs to this stream attempt, not the authenticated TCP
            // session. Keeping control alive lets Android display the real error instead of
            // treating a server process exit as a client-side crash/disconnect.
            StopPipeline();
            _send(OutgoingMessages.Error("ENCODER_UNAVAILABLE", ex.Message));
            _send(OutgoingMessages.StreamStopped());
            Console.Error.WriteLine($"[session] encoder unavailable: {ex.Message}");
            _state = SessionState.Ready;
        }
        catch (Exception ex)
        {
            StopPipeline();
            _send(OutgoingMessages.Error("STREAM_FAILED", ex.Message));
            _send(OutgoingMessages.StreamStopped());
            Console.Error.WriteLine($"[session] failed to start stream: {ex}");
            _state = SessionState.Ready;
        }
    }

    private string ResolveRequestedQuality(string? requested) =>
        requested != null ? ServerOptions.Normalize(requested) : ServerOptions.Normalize(_options.DefaultQuality);

    /// <summary>
    /// Resolves the authoritative streamed dimensions from the quality setting (PROTOCOL.md §2.3).
    /// "native" streams the source unchanged; "720p" downscales to 720 lines preserving aspect
    /// ratio, rounding the width to the nearest even value, never upscaling, minimum 2. NV12
    /// requires even dimensions.
    /// </summary>
    private (int width, int height) ResolveStreamSize(int srcWidth, int srcHeight)
    {
        if (_quality != "720p" || srcHeight <= 0 || srcWidth <= 0)
            return (srcWidth, srcHeight);

        int h = Math.Min(720, srcHeight);
        double exact = (double)h * srcWidth / srcHeight;
        int w = (int)(Math.Round(exact / 2.0, MidpointRounding.AwayFromZero) * 2); // round to nearest even
        h &= ~1;                                    // even
        if (w < 2) w = 2;
        if (h < 2) h = 2;
        if (w > srcWidth) w = srcWidth & ~1;        // clamp <= source; never upscale
        return (w, h);
    }

    private void OnStopStream()
    {
        if (!_streaming)
            return;
        StopPipeline();
        _send(OutgoingMessages.StreamStopped());
        _state = SessionState.Ready;
        Console.WriteLine("[session] streaming stopped.");
    }

    // ---- Dashboard commands (marshalled onto the control read loop) -----------------------

    /// <summary>Queues a Restart-stream request from the web dashboard (any thread).</summary>
    public void RequestRestart() => _commands.Enqueue(DashboardRestart);

    /// <summary>
    /// Drains queued dashboard commands. MUST be called only from the control read loop so that
    /// pipeline start/stop stays serialized with control-message handling.
    /// </summary>
    public void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            try { command(); }
            catch (Exception ex) { Console.Error.WriteLine($"[dashboard] command failed: {ex.Message}"); }
        }
    }

    private void DashboardRestart()
    {
        if (!_streaming)
            return; // nothing to restart; the dashboard only offers Restart while streaming
        StopPipeline();
        _state = SessionState.Ready;
        BeginStream(); // reuses the current bitrate cap, fps, and quality
        Console.WriteLine("[session] streaming restarted from dashboard.");
    }

    private void OnAudioStart()
    {
        if (_state != SessionState.Streaming || !_streaming)
            return;

        // Idempotent: Activity recreation or a repeated request only needs the negotiated
        // parameters again; it must not open a second WASAPI capture client.
        if (_audioStreaming && _audioCapture != null && _audioSender != null)
        {
            _send(OutgoingMessages.AudioStarted(_audioSender.Port));
            return;
        }

        // A device may have disappeared after a previous successful start. Clean up that
        // stopped instance so an explicit retry can reacquire the current default device.
        if (_audioCapture != null || _audioSender != null)
            StopAudio();

        try
        {
            _audioSender = new AudioSender(Ports.PreferredAudio, _clientAddress);
            _audioSender.Start();
            _audioCapture = new SystemAudioCapture(CurrentStreamPtsMs);
            _audioCapture.DataAvailable += OnAudioData;
            _audioCapture.Failed += OnAudioFailed;
            _audioCapture.Start();
            _audioStreaming = true;

            _send(OutgoingMessages.AudioStarted(_audioSender.Port));
            Console.WriteLine($"[audio] system output '{_audioCapture.DeviceName}' -> " +
                              $"48 kHz stereo PCM16, UDP {_audioSender.Port}.");
        }
        catch (Exception ex)
        {
            StopAudio();
            string message = string.IsNullOrWhiteSpace(ex.Message)
                ? "Windows system audio capture could not be started"
                : ex.Message;
            _send(OutgoingMessages.AudioUnavailable(message));
            Console.Error.WriteLine($"[audio] unavailable: {message}");
        }
    }

    private void OnMediaReady(ReadOnlySpan<byte> payload)
    {
        if (_state != SessionState.Streaming || !_streaming)
            return;
        var ready = Json.Deserialize<EndpointReadyMessage>(payload);
        if (ready == null || !(_sender?.SetClientPort(ready.Port) ?? false))
            Console.Error.WriteLine("[media] ignored invalid MEDIA_READY endpoint.");
    }

    private void OnAudioReady(ReadOnlySpan<byte> payload)
    {
        if (_state != SessionState.Streaming || !_audioStreaming)
            return;
        var ready = Json.Deserialize<EndpointReadyMessage>(payload);
        if (ready == null || !(_audioSender?.SetClientPort(ready.Port) ?? false))
            Console.Error.WriteLine("[audio] ignored invalid AUDIO_READY endpoint.");
    }

    private void OnGamepadStart(ReadOnlySpan<byte> payload)
    {
        if (_state != SessionState.Streaming || !_streaming)
            return;

        var message = Json.Deserialize<GamepadStartMessage>(payload);
        int requested = Math.Clamp(message?.Controllers ?? 1, 1, VirtualGamepadManager.MaxControllers);

        try
        {
            if (_gamepads == null)
            {
                _gamepads = new VirtualGamepadManager();
                _gamepads.Rumble += OnGamepadRumble;
            }

            int count = _gamepads.Start(requested);
            _send(OutgoingMessages.GamepadStarted(count));
            Console.WriteLine($"[gamepad] {count} virtual Xbox 360 controller(s) connected.");
        }
        catch (Exception ex)
        {
            StopGamepads();
            string messageText = DescribeGamepadError(ex);
            _send(OutgoingMessages.GamepadUnavailable(messageText));
            Console.Error.WriteLine($"[gamepad] unavailable: {messageText}");
        }
    }

    private void OnInputStart(ReadOnlySpan<byte> payload)
    {
        if (_state != SessionState.Streaming || !_streaming)
            return;

        var request = Json.Deserialize<InputStartMessage>(payload);
        bool mouseRequested = request?.Mouse == true;
        bool keyboardRequested = request?.Keyboard == true;
        if (!mouseRequested && !keyboardRequested)
        {
            _send(OutgoingMessages.InputUnavailable("The client did not request a supported input device"));
            return;
        }

        try
        {
            if (mouseRequested)
                _mouse ??= new RemoteMouseManager();
            else
                StopMouse();

            if (keyboardRequested)
                _keyboard ??= new RemoteKeyboardManager();
            else
                StopKeyboard();

            // Capture-fault cleanup runs off the control thread. If it won the race after
            // the initial state check, discard anything this request just created rather
            // than leaving an input manager alive after the stream ended.
            if (_state != SessionState.Streaming || !_streaming)
            {
                StopInput();
                return;
            }

            _send(OutgoingMessages.InputStarted(mouseRequested, keyboardRequested));
            string capabilities = mouseRequested && keyboardRequested
                ? "mouse and keyboard"
                : mouseRequested ? "mouse" : "keyboard";
            Console.WriteLine($"[input] authenticated remote {capabilities} enabled.");
        }
        catch (Exception ex)
        {
            StopInput();
            _send(OutgoingMessages.InputUnavailable(ex.Message));
        }
    }

    private void OnMouseButton(ReadOnlySpan<byte> payload)
    {
        if (_state != SessionState.Streaming || !_streaming)
            return;
        var mouse = Volatile.Read(ref _mouse);
        if (mouse == null)
            return;
        var message = Json.Deserialize<MouseButtonMessage>(payload);
        if (message == null)
            return;
        try
        {
            mouse.SetButton(message.Sequence, message.Button, message.Down);
        }
        catch (Exception ex)
        {
            StopInput();
            _send(OutgoingMessages.InputUnavailable(ex.Message));
        }
    }

    private void OnKeyboardKey(ReadOnlySpan<byte> payload)
    {
        if (_state != SessionState.Streaming || !_streaming)
            return;
        var keyboard = Volatile.Read(ref _keyboard);
        if (keyboard == null)
            return;
        var message = Json.Deserialize<KeyboardKeyMessage>(payload);
        if (message == null)
            return;
        try
        {
            keyboard.SetKey(message.Sequence, message.Usage, message.Down);
        }
        catch (Exception ex)
        {
            StopInput();
            _send(OutgoingMessages.InputUnavailable(ex.Message));
        }
    }

    // ---- Pipeline -------------------------------------------------------------------------

    private void StartPipeline()
    {
        _sender = new MediaSender(Ports.PreferredMedia, _clientAddress);
        _sender.OnGamepadState = OnGamepadState;
        _sender.OnMouseMotion = OnMouseMotion;
        _sender.OnClientConnected = OnMediaClientConnected;
        _sender.Start();

        _duplicator = new DesktopDuplicator();
        (_streamWidth, _streamHeight) = ResolveStreamSize(_duplicator.Width, _duplicator.Height);
        _converter = new Nv12Converter(
            _duplicator.Device,
            _duplicator.Width,
            _duplicator.Height,
            _streamWidth,
            _streamHeight,
            Fps);

        _currentBitrateKbps = Math.Min(8000, _maxBitrateKbps); // start bitrate (PROTOCOL.md §4)
        _encoder = EncoderFactory.Create(
            _duplicator.Device,
            _streamWidth,
            _streamHeight,
            Fps,
            _currentBitrateKbps);
        _encoder.OnEncodedFrame = OnEncoded;

        _frameId = 0;
        Interlocked.Exchange(ref _pipelineFaultHandling, 0);
        Interlocked.Exchange(ref _endpointIdrPending, 1);
        _lastNv12 = null;
        _capturedSinceEncode = 0;
        _cleanStreak = 0;
        _idrSinceLastStats = 0;
        _idrTimes.Clear();
        _bestCaptureToReceiveMs = int.MaxValue;
        _bestDecodeToSurfaceMs = int.MaxValue;
        _clockBaseUs = NowUs();
        _clock = Stopwatch.StartNew();

        _captureCts = new CancellationTokenSource();
        _captureThread = new Thread(() => CaptureLoop(_captureCts.Token))
        {
            IsBackground = true,
            Name = "capture",
        };
        _streaming = true;
        _captureThread.Start();
    }

    private void CaptureLoop(CancellationToken ct)
    {
        try
        {
            long frameIntervalTicks = Math.Max(1, Stopwatch.Frequency / Math.Max(1, Fps));
            long nextFrameTicks = Stopwatch.GetTimestamp();
            while (!ct.IsCancellationRequested)
            {
                if (_duplicator!.TryAcquire(100, out var bgra))
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    if (nowTicks < nextFrameTicks)
                        continue; // Desktop may present at 120/144/240 Hz; honor requested FPS.
                    nextFrameTicks = nowTicks + frameIntervalTicks;

                    if (Interlocked.Increment(ref _capturedSinceEncode) > Fps * 3)
                        throw new InvalidOperationException("The hardware encoder stopped producing frames");
                    var nv12 = _converter!.Convert(bgra);
                    Volatile.Write(ref _lastNv12, nv12);
                    SubmitCapture(nv12);
                }
                else if (Interlocked.Exchange(ref _endpointIdrPending, 0) != 0)
                {
                    // On a static desktop Desktop Duplication has no new frame after the
                    // endpoint is learned. Re-submit the latest NV12 surface as an IDR so the
                    // newly connected Android decoder still receives an initial picture.
                    var latest = Volatile.Read(ref _lastNv12);
                    if (latest != null)
                        SubmitCapture(latest, forceIdr: true);
                    else
                        Interlocked.Exchange(ref _endpointIdrPending, 1);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[capture] loop terminated: {ex.Message}");
            if (!ct.IsCancellationRequested &&
                Interlocked.CompareExchange(ref _pipelineFaultHandling, 1, 0) == 0)
            {
                // This cannot run on the capture thread: StopPipeline joins that thread and
                // tears down the encoder. Move it off-thread, but keep the TCP session alive.
                _ = Task.Run(() => StopFaultedPipeline(ex));
            }
        }
    }

    private void SubmitCapture(ID3D11Texture2D nv12, bool forceIdr = false)
    {
        if (forceIdr || Interlocked.Exchange(ref _endpointIdrPending, 0) != 0)
            _encoder!.RequestIdr();
        uint pts = (uint)(_clock!.ElapsedMilliseconds & 0xFFFFFFFF);
        _encoder!.Submit(nv12, pts);
    }

    private void StopFaultedPipeline(Exception ex)
    {
        // The capture thread returns immediately after scheduling us. If a user-initiated
        // STOP_STREAM won the race, its cleanup is already sufficient.
        if (!_streaming)
            return;

        StopPipeline();
        _state = SessionState.Ready;
        _send(OutgoingMessages.Error("STREAM_FAILED", ex.Message));
        _send(OutgoingMessages.StreamStopped());
        Console.Error.WriteLine("[session] stream stopped after capture/encoder fault; control session remains connected.");
    }

    private void OnEncoded(byte[] data, int length, bool keyframe, uint ptsMs)
    {
        uint id = _frameId++;
        Interlocked.Exchange(ref _capturedSinceEncode, 0);
        Interlocked.Increment(ref _encodedFrames);
        ushort pipelineDelayMs = (ushort)Math.Min(ushort.MaxValue, CurrentStreamPtsMs() - ptsMs);
        _sender?.SendFrame(data, length, id, keyframe, ptsMs, pipelineDelayMs);
    }

    private uint CurrentStreamPtsMs() =>
        _clock == null ? 0 : (uint)(_clock.ElapsedMilliseconds & 0xFFFFFFFF);

    private void OnAudioData(byte[] data, int length, uint captureEndPtsMs)
    {
        _audioSender?.SendPcm(data, length, captureEndPtsMs);
    }

    private void OnAudioFailed(string message)
    {
        _audioStreaming = false;
        _send(OutgoingMessages.AudioUnavailable(message));
        Console.Error.WriteLine($"[audio] capture stopped: {message}");
    }

    private void OnGamepadState(GamepadState state)
    {
        try
        {
            _gamepads?.Apply(state);
        }
        catch (Exception ex)
        {
            string message = DescribeGamepadError(ex);
            StopGamepads();
            _send(OutgoingMessages.GamepadUnavailable(message));
            Console.Error.WriteLine($"[gamepad] virtual controller stopped: {message}");
        }
    }

    private void OnGamepadRumble(int controllerId, byte largeMotor, byte smallMotor)
    {
        _send(OutgoingMessages.GamepadRumble(controllerId, largeMotor, smallMotor));
    }

    private void OnMouseMotion(MouseMotion motion)
    {
        if (!_streaming)
            return;
        var mouse = Volatile.Read(ref _mouse);
        if (mouse == null)
            return;
        try
        {
            CursorPosition? position = mouse.Apply(motion);
            if (position.HasValue)
                _sender?.SendCursorPosition(motion.Sequence, position.Value);
        }
        catch (Exception ex)
        {
            StopInput();
            _send(OutgoingMessages.InputUnavailable(ex.Message));
        }
    }

    private void OnMediaClientConnected()
    {
        if (!_streaming) return;
        Interlocked.Exchange(ref _endpointIdrPending, 1);
        Console.WriteLine("[media] client endpoint learned; scheduling an IDR.");
    }

    private void StopPipeline()
    {
        _streaming = false;
        StopInput();
        StopGamepads();
        StopAudio();
        try { _captureCts?.Cancel(); } catch { }
        try { _captureThread?.Join(1500); } catch { }
        _captureThread = null;

        try { _encoder?.Dispose(); } catch { }
        try { _converter?.Dispose(); } catch { }
        try { _duplicator?.Dispose(); } catch { }
        if (_sender != null)
        {
            _sender.OnGamepadState = null;
            _sender.OnMouseMotion = null;
            _sender.OnClientConnected = null;
        }
        try { _sender?.Dispose(); } catch { }
        _encoder = null; _converter = null; _duplicator = null; _sender = null;
        _lastNv12 = null;
        Interlocked.Exchange(ref _endpointIdrPending, 0);
        _captureCts?.Dispose(); _captureCts = null;
        _clock = null;
        _clockBaseUs = 0;
    }

    private void StopAudio()
    {
        _audioStreaming = false;
        if (_audioCapture != null)
        {
            _audioCapture.DataAvailable -= OnAudioData;
            _audioCapture.Failed -= OnAudioFailed;
        }
        try { _audioCapture?.Dispose(); } catch { }
        try { _audioSender?.Dispose(); } catch { }
        _audioCapture = null;
        _audioSender = null;
    }

    private void StopGamepads()
    {
        if (_gamepads != null)
            _gamepads.Rumble -= OnGamepadRumble;
        try { _gamepads?.Dispose(); } catch { }
        _gamepads = null;
    }

    private void StopMouse()
    {
        var mouse = Interlocked.Exchange(ref _mouse, null);
        try { mouse?.Dispose(); } catch { }
    }

    private void StopKeyboard()
    {
        var keyboard = Interlocked.Exchange(ref _keyboard, null);
        try { keyboard?.Dispose(); } catch { }
    }

    private void StopInput()
    {
        StopMouse();
        StopKeyboard();
    }

    // ---- Adaptation controller (PROTOCOL.md §4) -------------------------------------------

    private void OnRequestIdr()
    {
        Interlocked.Increment(ref _idrRequestTotal);
        _idrSinceLastStats++;

        long now = NowMs();

        // Rate-limit actual IDR generation to at most one per 300 ms.
        if (now - _lastIdrGenMs >= 300)
        {
            _encoder?.RequestIdr();
            _lastIdrGenMs = now;
        }

        // "Down" trigger: >= 2 REQUEST_IDR within 1 s.
        _idrTimes.Enqueue(now);
        while (_idrTimes.Count > 0 && now - _idrTimes.Peek() > 1000)
            _idrTimes.Dequeue();

        if (_idrTimes.Count >= 2)
        {
            AdaptDown();
            _idrTimes.Clear(); // avoid re-triggering off the same burst
        }
    }

    private void OnStats(ReadOnlySpan<byte> payload)
    {
        var s = Json.Deserialize<StatsMessage>(payload);
        if (s == null)
            return;

        LastClientFramesDropped = s.FramesDropped;
        int total = s.FramesOk + s.FramesDropped;
        double dropRate = total > 0 ? (double)s.FramesDropped / total : 0.0;

        if (s.CaptureToReceiveP95Ms >= 0)
            _bestCaptureToReceiveMs = Math.Min(_bestCaptureToReceiveMs, s.CaptureToReceiveP95Ms);
        if (s.DecodeToSurfaceP95Ms >= 0)
            _bestDecodeToSurfaceMs = Math.Min(_bestDecodeToSurfaceMs, s.DecodeToSurfaceP95Ms);

        bool latencyGrowing =
            (_bestCaptureToReceiveMs != int.MaxValue &&
             s.CaptureToReceiveP95Ms > _bestCaptureToReceiveMs + 15) ||
            (_bestDecodeToSurfaceMs != int.MaxValue &&
             s.DecodeToSurfaceP95Ms > _bestDecodeToSurfaceMs + 12);
        bool clean = s.FramesDropped == 0 && _idrSinceLastStats == 0 && !latencyGrowing;

        if (dropRate > 0.01 || latencyGrowing)
        {
            AdaptDown();
        }
        else if (clean)
        {
            _cleanStreak++;
            if (_cleanStreak >= 5)
            {
                AdaptUp();
                _cleanStreak = 0;
            }
        }
        else
        {
            _cleanStreak = 0;
        }

        _idrSinceLastStats = 0;
    }

    private void AdaptDown()
    {
        int next = Math.Max(ServerOptions.MinimumBitrateKbps, (int)(_currentBitrateKbps * 0.7));
        ApplyBitrate(next);
        _cleanStreak = 0;
    }

    private void AdaptUp()
    {
        int next = Math.Min(_maxBitrateKbps, (int)(_currentBitrateKbps * 1.1));
        ApplyBitrate(next);
    }

    private void ApplyBitrate(int kbps)
    {
        if (kbps == _currentBitrateKbps || _encoder == null)
            return;
        try
        {
            if (!_encoder.SetBitrate(kbps))
            {
                Console.Error.WriteLine(
                    $"[encoder] bitrate reconfiguration rejected; keeping {_currentBitrateKbps} kbps");
                return;
            }
            _currentBitrateKbps = kbps;
            _send(OutgoingMessages.Bitrate(kbps));
        }
        catch (Exception ex)
        {
            // Some vendor drivers expose hardware encode but reject live reconfiguration.
            // Keep the healthy stream at its previous target instead of dropping the session.
            Console.Error.WriteLine($"[encoder] bitrate reconfiguration rejected: {ex.Message}");
        }
    }

    // ---- Helpers --------------------------------------------------------------------------

    private void ResolveDisplay()
    {
        if (_displayResolved)
            return;
        try
        {
            (_displayWidth, _displayHeight) = DesktopDuplicator.GetPrimaryDisplaySize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[session] could not query display size ({ex.Message}); using {_displayWidth}x{_displayHeight}.");
        }
        _displayResolved = true;
    }

    private static void PrintPin(string pin)
    {
        Console.WriteLine();
        Console.WriteLine("  +--------------------------------------+");
        Console.WriteLine("  |  DeskStream pairing                  |");
        Console.WriteLine($"  |  Enter this PIN on your phone:  {pin} |");
        Console.WriteLine("  |  (valid for 60 seconds)              |");
        Console.WriteLine("  +--------------------------------------+");
        Console.WriteLine();
    }

    private static string DescribeGamepadError(Exception ex)
    {
        string type = ex.GetType().Name;
        if (type.Contains("BusNotFound", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("DllNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return "ViGEmBus is not installed. Install the official ViGEmBus 1.22 driver, then restart DeskStream.";
        }
        if (type.Contains("BusVersionMismatch", StringComparison.OrdinalIgnoreCase))
            return "The installed ViGEmBus driver is incompatible. Install ViGEmBus 1.22 and restart DeskStream.";
        return string.IsNullOrWhiteSpace(ex.Message)
            ? "The Windows virtual Xbox controller could not be created."
            : ex.Message;
    }

    public void Dispose()
    {
        StopPipeline();
    }
}
