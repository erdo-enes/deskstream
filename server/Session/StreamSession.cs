using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using DeskStreamer.Server.Audio;
using DeskStreamer.Server.Capture;
using DeskStreamer.Server.Encode;
using DeskStreamer.Server.Input;
using DeskStreamer.Server.Logging;
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
    // Consecutive stats intervals where p95 latency exceeded the learned baseline. We require a
    // sustained rise (>=2 intervals) before cutting, so a single spike (transient / startup-IDR-
    // contaminated p95 window) cannot slash bitrate.
    private int _latencyGrowingStreak;

    // AIMD-with-memory: remember the level a loss episode was found at (the "breaking point")
    // and converge additively under it instead of sawtoothing through it. The ceiling slowly
    // recovers on clean intervals so a transient blip does not cap quality forever.
    private int _bitrateCeilingKbps = ServerOptions.DefaultMaxBitrateKbps;
    private long _lastAdaptDownMs = long.MinValue / 4;  // "far past" so the first cut is never debounced
    private long _lastCeilingRaiseMs;
    private long _lastCeilingCutMs = long.MinValue / 4;  // time of the last REAL ceiling reduction

    // Stats surfaced to the console (Program reads these)
    private long _encodedFrames;
    private long _captureSubmittedFrames;
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
    public int LastClientFramesOk { get; private set; }
    public int LastClientFramesAssembled { get; private set; } = -1;
    public int LastClientAssemblyDrops { get; private set; } = -1;
    public int LastClientDecoderDrops { get; private set; } = -1;
    public int LastClientFecRecovered { get; private set; } = -1;
    public int LastClientVideoPackets { get; private set; } = -1;
    public int LastClientFecPackets { get; private set; } = -1;
    public int LastClientStatsIntervalMs { get; private set; }
    public int LastServerPipelineP95Ms { get; private set; } = -1;
    public int LastCaptureToReceiveP95Ms { get; private set; } = -1;
    public int LastDecodeToSurfaceP95Ms { get; private set; } = -1;
    public long LastClientStatsAgeMs => _lastClientStatsAtMs <= 0 ? long.MaxValue : NowMs() - _lastClientStatsAtMs;
    private long _lastClientStatsAtMs;

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
    public long CaptureSubmittedFrames => Interlocked.Read(ref _captureSubmittedFrames);
    public long IdrRequestTotal => Interlocked.Read(ref _idrRequestTotal);
    public long AudioPacketsSent => _audioSender?.PacketsSent ?? 0;
    public long AudioBytesSent => _audioSender?.BytesSent ?? 0;
    public bool MediaEndpointReady => _sender?.HasClient == true;
    public long MediaFramesSent => _sender?.FramesSent ?? 0;
    public long MediaBytesSent => _sender?.BytesSent ?? 0;
    public long MediaPacketsSent => _sender?.PacketsSent ?? 0;
    public long MediaSendFailures => _sender?.SendFailures ?? 0;
    public long MediaPacingWaitUs => _sender?.PacingWaitUs ?? 0;

    // Capture-pipeline diagnostics (Program composes the 1 Hz stats line from per-second deltas).
    public long CaptureRealFrames => _duplicator?.RealFrames ?? 0;
    public long CapturePointerOnly => _duplicator?.PointerOnlyFrames ?? 0;
    public long CaptureTimeouts => _duplicator?.Timeouts ?? 0;
    public long CaptureAccumulatedFrames => _duplicator?.AccumulatedFrames ?? 0;

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
            AsyncLogger.Info($"[session] Client '{hello.ClientName}' ({_clientAddress}) connected and authenticated successfully.");
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
        AsyncLogger.Info($"[pairing] Generated pairing PIN {_pin} for client '{_clientName}'");
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
            AsyncLogger.Info($"[session] Pairing succeeded for client '{_clientName}' ({_clientId})");
        }
        else
        {
            _pinAttempts--;
            AsyncLogger.Warn($"[pairing] Incorrect PIN entered for client '{_clientName}'. Attempts remaining: {_pinAttempts}");
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
            // Dimensions are only known after StartPipeline queries the duplicator, so this
            // pre-start line intentionally omits them (they were previously logged as 0x0 on the
            // very first start, before any duplicator existed). The post-start line below reports
            // the authoritative resolved dimensions.
            AsyncLogger.Info($"[session] Stream starting: {_quality}@{Fps}, max bitrate {_maxBitrateKbps} kbps");
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
            AsyncLogger.Info($"[session] Stream successfully started on media port {_sender.Port}. Encoder: {_encoder.BackendName}");
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
            AsyncLogger.Error($"[session] Encoder unavailable: {ex.Message}");
            _state = SessionState.Ready;
        }
        catch (Exception ex)
        {
            StopPipeline();
            _send(OutgoingMessages.Error("STREAM_FAILED", ex.Message));
            _send(OutgoingMessages.StreamStopped());
            Console.Error.WriteLine($"[session] failed to start stream: {ex}");
            AsyncLogger.Error($"[session] Failed to start stream: {ex}");
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
        AsyncLogger.Info("[session] Streaming stopped by client.");
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
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dashboard] command failed: {ex.Message}");
                AsyncLogger.Error($"[dashboard] Command failed: {ex.Message}");
            }
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
        AsyncLogger.Info("[session] Streaming restarted from dashboard command.");
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
            AsyncLogger.Info($"[audio] System output '{_audioCapture.DeviceName}' started -> 48 kHz stereo PCM16, UDP {_audioSender.Port}.");
        }
        catch (Exception ex)
        {
            StopAudio();
            string message = string.IsNullOrWhiteSpace(ex.Message)
                ? "Windows system audio capture could not be started"
                : ex.Message;
            _send(OutgoingMessages.AudioUnavailable(message));
            Console.Error.WriteLine($"[audio] unavailable: {message}");
            AsyncLogger.Error($"[audio] Audio unavailable: {message}");
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
            AsyncLogger.Info($"[gamepad] {count} virtual Xbox 360 controller(s) connected successfully.");
        }
        catch (Exception ex)
        {
            StopGamepads();
            string messageText = DescribeGamepadError(ex);
            _send(OutgoingMessages.GamepadUnavailable(messageText));
            Console.Error.WriteLine($"[gamepad] unavailable: {messageText}");
            AsyncLogger.Error($"[gamepad] Gamepads unavailable: {messageText}");
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
            AsyncLogger.Info($"[input] Authenticated remote {capabilities} enabled successfully.");
        }
        catch (Exception ex)
        {
            StopInput();
            _send(OutgoingMessages.InputUnavailable(ex.Message));
            AsyncLogger.Error($"[input] Input failed to start: {ex.Message}");
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

        _currentBitrateKbps = Math.Min(12000, _maxBitrateKbps); // start bitrate (PROTOCOL.md §4)
        _encoder = EncoderFactory.Create(
            _duplicator.Device,
            _streamWidth,
            _streamHeight,
            Fps,
            _currentBitrateKbps);
        _encoder.OnEncodedFrame = OnEncoded;

        // Shape only oversized IDR bursts (token-bucket); normal frames pass untouched.
        _sender!.SetPacingRate(_currentBitrateKbps, Fps);

        _frameId = 0;
        Interlocked.Exchange(ref _encodedFrames, 0);
        Interlocked.Exchange(ref _captureSubmittedFrames, 0);
        Interlocked.Exchange(ref _pipelineFaultHandling, 0);
        Interlocked.Exchange(ref _endpointIdrPending, 1);
        _lastNv12 = null;
        _capturedSinceEncode = 0;
        _cleanStreak = 0;
        _idrSinceLastStats = 0;
        _bitrateCeilingKbps = _maxBitrateKbps;
        _lastAdaptDownMs = long.MinValue / 4;
        _lastCeilingCutMs = long.MinValue / 4;
        _lastCeilingRaiseMs = NowMs();
        _idrTimes.Clear();
        _bestCaptureToReceiveMs = int.MaxValue;
        _bestDecodeToSurfaceMs = int.MaxValue;
        _latencyGrowingStreak = 0;
        LastClientFramesDropped = 0;
        LastClientFramesOk = 0;
        LastClientFramesAssembled = -1;
        LastClientAssemblyDrops = -1;
        LastClientDecoderDrops = -1;
        LastClientFecRecovered = -1;
        LastClientVideoPackets = -1;
        LastClientFecPackets = -1;
        LastClientStatsIntervalMs = 0;
        LastServerPipelineP95Ms = -1;
        LastCaptureToReceiveP95Ms = -1;
        LastDecodeToSurfaceP95Ms = -1;
        _lastClientStatsAtMs = 0;
        _clockBaseUs = NowUs();
        _clock = Stopwatch.StartNew();

        _captureCts = new CancellationTokenSource();
        _captureThread = new Thread(() => CaptureLoop(_captureCts.Token))
        {
            IsBackground = true,
            Name = "capture",
            Priority = ThreadPriority.Highest,
        };
        _streaming = true;
        _captureThread.Start();
    }

    /// <summary>
    /// Capture loop — simple blocking acquire with continue-based rate limiting.
    ///
    /// This is the v0.4.0 approach that was proven to work for 1080p60 gaming:
    ///   1. TryAcquire(100) blocks until DXGI has a frame (up to 100ms). At 60fps the
    ///      frame arrives in ~16ms, so the call returns naturally — DXGI IS the pacer.
    ///   2. If the frame arrived before the next interval (120/144Hz display), continue
    ///      skips encoding and re-acquires. This prevents >60fps without sleeping.
    ///   3. No pre-acquire sleep, no post-false sleep — the blocking acquire handles both
    ///      pacing and idle. MMCSS + GPUThreadPriority=7 ensure the thread gets scheduled
    ///      under 100% game load.
    ///
    /// The v0.5.x changes (micro-pacing sleep, Sleep(50) on false, frame pacing group)
    /// all introduced timing issues that caused stutter. This loop is deliberately simple.
    /// </summary>
    private void CaptureLoop(CancellationToken ct)
    {
        IntPtr mmcss = NativeMethods.JoinGamesMmcss();
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
            AsyncLogger.Error($"[capture] Capture loop terminated exceptionally: {ex.Message}");
            if (!ct.IsCancellationRequested &&
                Interlocked.CompareExchange(ref _pipelineFaultHandling, 1, 0) == 0)
            {
                _ = Task.Run(() => StopFaultedPipeline(ex));
            }
        }
        finally
        {
            NativeMethods.RevertGamesMmcss(mmcss);
        }
    }

    private void SubmitCapture(ID3D11Texture2D nv12, bool forceIdr = false)
    {
        if (forceIdr || Interlocked.Exchange(ref _endpointIdrPending, 0) != 0)
            _encoder!.RequestIdr();
        uint pts = (uint)(_clock!.ElapsedMilliseconds & 0xFFFFFFFF);
        Interlocked.Increment(ref _captureSubmittedFrames);
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
        AsyncLogger.Error($"[session] Stream stopped after capture/encoder fault: {ex.Message}");
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
        AsyncLogger.Error($"[audio] Capture stopped: {message}");
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
            AsyncLogger.Error($"[gamepad] Virtual controller stopped: {message}");
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
        AsyncLogger.Info("[media] Client media UDP endpoint learned; scheduling an IDR frame.");
    }

    private void StopPipeline()
    {
        _streaming = false;
        AsyncLogger.Info("[session] Stopping stream pipeline.");
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

        // "Down" trigger: >= 3 REQUEST_IDR within 2 s indicates sustained packet loss,
        // not just a single transient re-request from a WiFi hiccup.
        _idrTimes.Enqueue(now);
        while (_idrTimes.Count > 0 && now - _idrTimes.Peek() > 2000)
            _idrTimes.Dequeue();

        if (_idrTimes.Count >= 3)
        {
            AdaptDown($"IDR burst {_idrTimes.Count}/2s");
            _idrTimes.Clear(); // avoid re-triggering off the same burst
        }
    }

    private void OnStats(ReadOnlySpan<byte> payload)
    {
        var s = Json.Deserialize<StatsMessage>(payload);
        if (s == null)
            return;

        LastClientFramesDropped = s.FramesDropped;
        LastClientFramesOk = s.FramesOk;
        LastClientFramesAssembled = s.FramesAssembled;
        LastClientAssemblyDrops = s.AssemblyFramesDropped;
        LastClientDecoderDrops = s.DecoderFramesDropped;
        LastClientFecRecovered = s.FecPacketsRecovered;
        LastClientVideoPackets = s.VideoPacketsReceived;
        LastClientFecPackets = s.FecPacketsReceived;
        LastClientStatsIntervalMs = s.IntervalMs;
        LastServerPipelineP95Ms = s.ServerPipelineP95Ms;
        LastCaptureToReceiveP95Ms = s.CaptureToReceiveP95Ms;
        LastDecodeToSurfaceP95Ms = s.DecodeToSurfaceP95Ms;
        _lastClientStatsAtMs = NowMs();

        // Skip adaptation during the first 2 seconds to avoid startup transient triggers.
        if (_clock == null || _clock.ElapsedMilliseconds < 2000)
            return;

        int total = s.FramesOk + s.FramesDropped;
        double dropRate = total > 0 ? (double)s.FramesDropped / total : 0.0;

        if (s.CaptureToReceiveP95Ms >= 0)
            _bestCaptureToReceiveMs = Math.Min(_bestCaptureToReceiveMs, s.CaptureToReceiveP95Ms);
        if (s.DecodeToSurfaceP95Ms >= 0)
            _bestDecodeToSurfaceMs = Math.Min(_bestDecodeToSurfaceMs, s.DecodeToSurfaceP95Ms);

        // Widen latency margins substantially: WiFi p95 jitter of 20-40ms is normal and should
        // not trigger cuts. Only react if capture-to-receive grows by >40ms or decode-to-surface
        // by >25ms above the learned floor.
        bool latencyGrowingNow =
            (_bestCaptureToReceiveMs != int.MaxValue &&
             s.CaptureToReceiveP95Ms > _bestCaptureToReceiveMs + 40) ||
            (_bestDecodeToSurfaceMs != int.MaxValue &&
             s.DecodeToSurfaceP95Ms > _bestDecodeToSurfaceMs + 25);

        // Require the rise to persist across >=3 consecutive intervals before reacting.
        // WiFi produces frequent 1-2 interval spikes that are not real congestion.
        _latencyGrowingStreak = latencyGrowingNow ? _latencyGrowingStreak + 1 : 0;
        bool latencySustained = _latencyGrowingStreak >= 3;

        // Require a meaningful loss: >5 frames dropped or >5% loss rate. At 60fps, dropping
        // 3-4 frames per second is typical WiFi behavior, not network congestion.
        bool dropCongestion = s.FramesDropped > 5 || dropRate > 0.05;
        bool clean = s.FramesDropped == 0 && _idrSinceLastStats == 0 && !latencyGrowingNow;

        if (dropCongestion || latencySustained)
        {
            string reason = dropCongestion
                ? $"drops {s.FramesDropped}/{total} ({dropRate:P1})"
                : $"latency p95 cap {s.CaptureToReceiveP95Ms}>{BaselineStr(_bestCaptureToReceiveMs)}+15 " +
                  $"dec {s.DecodeToSurfaceP95Ms}>{BaselineStr(_bestDecodeToSurfaceMs)}+12 x{_latencyGrowingStreak}";
            AdaptDown(reason);
        }
        else
        {
            // Ceiling recovery, independent of the (stricter) clean streak: on any quiet interval
            // nudge the remembered breaking point back toward the hard max so a stale or transient
            // episode cannot cap quality for minutes. Accelerate once a real cut is well past.
            // Ceiling recovery: on any clean interval, aggressively raise the remembered
            // breaking point back toward the hard max. The old 10s/5% cadence was far too slow
            // and left the stream capped at low quality for minutes after a single blip.
            if (s.FramesDropped == 0 && !latencyGrowingNow && _bitrateCeilingKbps < _maxBitrateKbps)
            {
                long now = NowMs();
                if (now - _lastCeilingRaiseMs >= 5_000)
                {
                    double factor = now - _lastCeilingCutMs >= 15_000 ? 1.40 : 1.15;
                    _bitrateCeilingKbps = Math.Min(_maxBitrateKbps, (int)(_bitrateCeilingKbps * factor));
                    _lastCeilingRaiseMs = now;
                }
            }

            if (clean)
            {
                _cleanStreak++;
                // Require only 2 clean intervals before trying to climb back up.
                // The old value of 5 meant 5+ seconds of perfect delivery before any
                // recovery, which was far too conservative for WiFi environments.
                if (_cleanStreak >= 2)
                {
                    AdaptUp();
                    _cleanStreak = 0;
                }
            }
            else
            {
                _cleanStreak = 0;
            }
        }

        _idrSinceLastStats = 0;
    }

    private static string BaselineStr(int best) => best == int.MaxValue ? "-" : best.ToString();

    /// <summary>
    /// Re-learns the p95 latency baseline from scratch. Called after any real bitrate change (the
    /// operating point moved) and after a floor no-op DOWN (so a chronically-high "growing" signal
    /// at the floor cannot pin the stream there forever).
    /// </summary>
    private void ResetLatencyBaseline()
    {
        _bestCaptureToReceiveMs = int.MaxValue;
        _bestDecodeToSurfaceMs = int.MaxValue;
        _latencyGrowingStreak = 0;
    }

    private void AdaptDown(string reason)
    {
        long now = NowMs();
        // Debounce: one loss episode routinely produces both an OnRequestIdr burst and an OnStats
        // drop report. Collapsing them into a single reaction kills the double-slash sawtooth.
        // Widen to 3s to prevent rapid successive cuts on WiFi.
        if (now - _lastAdaptDownMs < 3000)
            return;

        // Gentler cut: only 20% reduction instead of 30%. This prevents the catastrophic
        // compound crash (0.7^4 = 0.24x in 6 seconds) that the old algorithm suffered.
        int next = Math.Max(ServerOptions.MinimumBitrateKbps, (int)(_currentBitrateKbps * 0.80));

        // Floor no-op: the cut cannot lower an already-floored bitrate. Re-baseline so
        // latency signals don't keep firing, hold the debounce, and log at DEBUG.
        if (next >= _currentBitrateKbps)
        {
            ResetLatencyBaseline();
            _lastAdaptDownMs = now;
            AsyncLogger.Debug($"[adaptation] DOWN suppressed at floor {_currentBitrateKbps} kbps ({reason}); latency baseline reset");
            return;
        }

        _lastAdaptDownMs = now;
        _lastCeilingCutMs = now;
        // Set ceiling at 95% of the pre-cut level (not 85%) so recovery has room to climb
        // back close to where it was, rather than being trapped far below.
        _bitrateCeilingKbps = Math.Max(ServerOptions.MinimumBitrateKbps, (int)(_currentBitrateKbps * 0.95));
        AsyncLogger.Info($"[adaptation] DOWN {_currentBitrateKbps} -> {next} kbps ({reason}; ceiling {_bitrateCeilingKbps})");
        ApplyBitrate(next);
        _cleanStreak = 0;
    }

    private void AdaptUp()
    {
        int ceiling = Math.Min(_maxBitrateKbps, _bitrateCeilingKbps);
        // Use 15% multiplicative increase for faster recovery. Near the ceiling (within 10%),
        // switch to additive +1000 kbps so we probe gently rather than overshooting.
        bool additive = _currentBitrateKbps >= (int)(0.90 * ceiling);
        int next = additive
            ? Math.Min(ceiling, _currentBitrateKbps + 1000)
            : Math.Min(ceiling, (int)(_currentBitrateKbps * 1.15));
        if (next == _currentBitrateKbps)
            return; // already at the ceiling; nothing to raise
        AsyncLogger.Info($"[adaptation] UP {_currentBitrateKbps} -> {next} kbps ({(additive ? "additive" : "x1.15")}; ceiling {ceiling})");
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
                AsyncLogger.Warn($"[encoder] Bitrate reconfiguration to {kbps} kbps rejected by the hardware encoder; keeping {_currentBitrateKbps} kbps");
                return;
            }
            _currentBitrateKbps = kbps;
            _sender?.SetPacingRate(_currentBitrateKbps, Fps);
            _send(OutgoingMessages.Bitrate(kbps));
            AsyncLogger.Info($"[encoder] Bitrate reconfigured successfully to {kbps} kbps");

            // The operating point moved: re-learn the p95 latency baseline from scratch.
            ResetLatencyBaseline();
        }
        catch (Exception ex)
        {
            // Some vendor drivers expose hardware encode but reject live reconfiguration.
            // Keep the healthy stream at its previous target instead of dropping the session.
            Console.Error.WriteLine($"[encoder] bitrate reconfiguration rejected: {ex.Message}");
            AsyncLogger.Error($"[encoder] Bitrate reconfiguration failed: {ex.Message}");
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
