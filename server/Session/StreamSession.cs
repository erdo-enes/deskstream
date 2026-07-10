using System.Diagnostics;
using System.Net;
using DeskStreamer.Server.Audio;
using DeskStreamer.Server.Capture;
using DeskStreamer.Server.Encode;
using DeskStreamer.Server.Input;
using DeskStreamer.Server.Net;
using DeskStreamer.Server.Protocol;

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

    private SessionState _state = SessionState.AwaitingHello;
    private string _clientId = "";

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
    private DesktopDuplicator? _duplicator;
    private Nv12Converter? _converter;
    private H264Encoder? _encoder;
    private Thread? _captureThread;
    private CancellationTokenSource? _captureCts;
    private Stopwatch? _clock;
    private uint _frameId;
    private volatile bool _streaming;
    private volatile bool _audioStreaming;

    // Adaptation
    private int _maxBitrateKbps = 20000;
    private int _currentBitrateKbps;
    private int _cleanStreak;
    private int _idrSinceLastStats;
    private readonly Queue<long> _idrTimes = new();
    private long _lastIdrGenMs = -1000;
    private static readonly long StartTicks = Stopwatch.GetTimestamp();

    // Stats surfaced to the console (Program reads these)
    private long _encodedFrames;
    private long _idrRequestTotal;
    public SessionState State => _state;
    public bool Streaming => _streaming;
    public bool AudioStreaming => _audioStreaming;
    public int GamepadCount => _gamepads?.Count ?? 0;
    public int Fps { get; private set; } = 60;
    public int CurrentBitrateKbps => _currentBitrateKbps;
    public int LastClientFramesDropped { get; private set; }
    public long EncodedFrames => Interlocked.Read(ref _encodedFrames);
    public long IdrRequestTotal => Interlocked.Read(ref _idrRequestTotal);
    public long AudioPacketsSent => _audioSender?.PacketsSent ?? 0;
    public long AudioBytesSent => _audioSender?.BytesSent ?? 0;

    public StreamSession(
        Action<object> send,
        Action requestClose,
        PairingManager pairing,
        string serverName,
        IPAddress? clientAddress)
    {
        _send = send;
        _requestClose = requestClose;
        _pairing = pairing;
        _serverName = serverName;
        _clientAddress = clientAddress;
    }

    private static long NowMs() =>
        (Stopwatch.GetTimestamp() - StartTicks) * 1000 / Stopwatch.Frequency;

    // ---- Message dispatch -----------------------------------------------------------------

    public void HandleMessage(string type, ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case "HELLO": OnHello(payload); break;
            case "PAIR_REQUEST": OnPairRequest(); break;
            case "PAIR_CODE": OnPairCode(payload); break;
            case "START_STREAM": OnStartStream(payload); break;
            case "AUDIO_START": OnAudioStart(); break;
            case "GAMEPAD_START": OnGamepadStart(payload); break;
            case "GAMEPAD_STOP": StopGamepads(); break;
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
        _maxBitrateKbps = msg is { MaxBitrateKbps: > 0 } ? msg.MaxBitrateKbps : 20000;
        Fps = msg is { Fps: >= 15 and <= 240 } ? msg.Fps : 60;

        try
        {
            StartPipeline();
            _send(OutgoingMessages.StreamStarted(_sender!.Port, _duplicator!.Width, _duplicator.Height, Fps));
            _state = SessionState.Streaming;
            Console.WriteLine($"[session] streaming started: {_duplicator.Width}x{_duplicator.Height}@{Fps}, " +
                              $"start bitrate {_currentBitrateKbps} kbps, media port {_sender.Port}.");
        }
        catch (EncoderUnavailableException ex)
        {
            // Hard capability failure — do not fall back to a black screen (per spec).
            StopPipeline();
            _send(OutgoingMessages.Error("ENCODER_UNAVAILABLE", ex.Message));
            Console.Error.WriteLine();
            Console.Error.WriteLine("FATAL: " + ex.Message);
            Console.Error.WriteLine("DeskStream requires a hardware H.264 encoder. Exiting.");
            Environment.Exit(2);
        }
        catch (Exception ex)
        {
            StopPipeline();
            _send(OutgoingMessages.Error("STREAM_FAILED", ex.Message));
            Console.Error.WriteLine($"[session] failed to start stream: {ex}");
            _state = SessionState.Ready;
        }
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

    // ---- Pipeline -------------------------------------------------------------------------

    private void StartPipeline()
    {
        _sender = new MediaSender(Ports.PreferredMedia, _clientAddress);
        _sender.OnGamepadState = OnGamepadState;
        _sender.Start();

        _duplicator = new DesktopDuplicator();
        _converter = new Nv12Converter(_duplicator.Device, _duplicator.Width, _duplicator.Height, Fps);

        _currentBitrateKbps = Math.Min(8000, _maxBitrateKbps); // start bitrate (PROTOCOL.md §4)
        _encoder = new H264Encoder(_duplicator.Device, _duplicator.Width, _duplicator.Height, Fps, _currentBitrateKbps);
        _encoder.OnEncodedFrame = OnEncoded;

        _frameId = 0;
        _cleanStreak = 0;
        _idrSinceLastStats = 0;
        _idrTimes.Clear();
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
            while (!ct.IsCancellationRequested)
            {
                if (_duplicator!.TryAcquire(100, out var bgra))
                {
                    var nv12 = _converter!.Convert(bgra);
                    uint pts = (uint)(_clock!.ElapsedMilliseconds & 0xFFFFFFFF);
                    _encoder!.Submit(nv12, pts);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[capture] loop terminated: {ex.Message}");
        }
    }

    private void OnEncoded(byte[] data, int length, bool keyframe, uint ptsMs)
    {
        uint id = _frameId++;
        Interlocked.Increment(ref _encodedFrames);
        _sender?.SendFrame(data, length, id, keyframe, ptsMs);
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

    private void StopPipeline()
    {
        _streaming = false;
        StopGamepads();
        StopAudio();
        try { _captureCts?.Cancel(); } catch { }
        try { _captureThread?.Join(1500); } catch { }
        _captureThread = null;

        try { _encoder?.Dispose(); } catch { }
        try { _converter?.Dispose(); } catch { }
        try { _duplicator?.Dispose(); } catch { }
        if (_sender != null)
            _sender.OnGamepadState = null;
        try { _sender?.Dispose(); } catch { }
        _encoder = null; _converter = null; _duplicator = null; _sender = null;
        _captureCts?.Dispose(); _captureCts = null;
        _clock = null;
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

        bool clean = s.FramesDropped == 0 && _idrSinceLastStats == 0;

        if (dropRate > 0.02)
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
        int next = Math.Max(2000, (int)(_currentBitrateKbps * 0.7));
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
        _currentBitrateKbps = kbps;
        _encoder.SetBitrate(kbps);
        _send(OutgoingMessages.Bitrate(kbps));
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
