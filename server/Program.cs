using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.InteropServices;
using DeskStreamer.Server.Logging;
using DeskStreamer.Server.Net;
using DeskStreamer.Server.Protocol;
using DeskStreamer.Server.Service;
using DeskStreamer.Server.Session;
using DeskStreamer.Server.Web;

// ---- Flag parsing ------------------------------------------------------------------------

bool HasFlag(string flag) =>
    args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

string? FlagValue(string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

// Autostart management acts then exits immediately.
if (HasFlag("--install-autostart"))
    return Autostart.InstallAutostart(HasFlag("--elevated"));
if (HasFlag("--uninstall-autostart"))
    return Autostart.Uninstall();

bool headless = HasFlag("--headless");
bool noWeb = HasFlag("--no-web");
bool webLan = HasFlag("--web-lan");

string appLogPath = Path.Combine(AppContext.BaseDirectory, "deskstream.app.log");
AsyncLogger.Initialize(appLogPath);

int webPort = 47810;
if (FlagValue("--web-port") is { } portText && int.TryParse(portText, out int parsedPort)
    && parsedPort is > 0 and < 65536)
{
    webPort = parsedPort;
}

int maxBitrateKbps = ServerOptions.DefaultMaxBitrateKbps;
if (FlagValue("--max-bitrate-kbps") is { } bitrateText
    && int.TryParse(bitrateText, out int parsedBitrate))
{
    maxBitrateKbps = parsedBitrate;
}

string defaultQuality = FlagValue("--quality") is { } requestedQuality
    ? ServerOptions.Normalize(requestedQuality)
    : "720p";

var options = new ServerOptions
{
    DefaultQuality = defaultQuality,
    MaxBitrateKbps = maxBitrateKbps,
};

// In headless mode there is no interactive console (the logon task has no window). Redirect all
// console output — including the pairing PIN box — to a log file next to the executable. The PIN
// is also always available through the web dashboard.
if (headless)
{
    // AppContext.BaseDirectory is the published executable's directory and also behaves
    // correctly for `dotnet run`; Environment.ProcessPath would point at dotnet.exe there.
    string exeDir = AppContext.BaseDirectory;
    string logPath = Path.Combine(exeDir, "deskstream.log");
    string previousLogPath = Path.Combine(exeDir, "deskstream.previous.log");
    // Keep at most the current and immediately previous session. Headless mode can run for
    // months, and the pairing PIN is sensitive enough that an unbounded append-only history is
    // undesirable. The 1 Hz stats line is also suppressed below; the dashboard owns live stats.
    try
    {
        if (File.Exists(logPath))
            File.Move(logPath, previousLogPath, overwrite: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Logging must not prevent desktop capture from starting. The current file is truncated
        // below if rotation was unavailable, which still preserves the bounded-history promise.
        Console.Error.WriteLine($"[headless] previous log could not be rotated: {ex.Message}");
    }
    var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
    Console.SetOut(logWriter);
    Console.SetError(logWriter);
    Console.WriteLine($"=== DeskStream headless start {DateTimeOffset.Now:O} ===");
    AsyncLogger.Info("Running in headless mode.");
}

// ---- Startup -----------------------------------------------------------------------------

if (OperatingSystem.IsWindows())
{
    try { NativeMethods.TimeBeginPeriod(1); } catch { }
    // Give the whole process scheduling headroom so capture/encode threads are not starved by
    // a foreground fullscreen game. Guarded: a failure must never prevent the server starting.
    try { NativeMethods.SetPriorityClass(NativeMethods.GetCurrentProcess(), NativeMethods.HIGH_PRIORITY_CLASS); } catch { }
}

// Favor latency over throughput in the GC (hot path is otherwise allocation-free).
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

string hostname = Dns.GetHostName();
var listenIps = LocalIPv4Addresses().ToList();

Console.WriteLine("DeskStream server");
Console.WriteLine("=================");
Console.WriteLine($"Host: {hostname}");
Console.WriteLine("Listening on:");
AsyncLogger.Info($"DeskStream Server starting on host: {hostname}");
foreach (var ip in listenIps)
{
    Console.WriteLine($"  {ip}   (discovery UDP {Ports.Discovery}, control TCP {Ports.Control}, " +
                      $"video UDP {Ports.PreferredMedia}, audio UDP {Ports.PreferredAudio})");
    AsyncLogger.Info($"Listening on: {ip} (discovery={Ports.Discovery}, control={Ports.Control}, video={Ports.PreferredMedia}, audio={Ports.PreferredAudio})");
}
Console.WriteLine();
Console.WriteLine($"Default stream quality: {options.DefaultQuality}");
Console.WriteLine($"Maximum stream bitrate: {options.MaxBitrateKbps} kbps");
AsyncLogger.Info($"Default stream quality: {options.DefaultQuality}");
AsyncLogger.Info($"Maximum stream bitrate: {options.MaxBitrateKbps} kbps");
string firewallPorts = "UDP 47800/47802/47803 and TCP 47801";
if (webLan && !noWeb)
    firewallPorts += $" plus TCP {webPort} for the LAN dashboard";
Console.WriteLine($"Firewall: allow {firewallPorts} on the Private network profile.");
Console.WriteLine("Waiting for a client to connect...");
Console.WriteLine();
AsyncLogger.Info($"Firewall requirements: allow {firewallPorts} on the Private network profile.");
AsyncLogger.Info("Waiting for a client to connect...");

var pairing = new PairingManager();

using var discovery = new DiscoveryResponder(hostname);
using var control = new ControlServer(pairing, hostname, options);
using var web = noWeb ? null : new WebDashboard(control, options, hostname, listenIps, webLan, webPort);

discovery.Start();
control.Start();
web?.Start();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

// 1 Hz status line while streaming.
long prevEncoded = 0;
long prevIdr = 0;
long prevAudioBytes = 0;
long prevMediaFrames = 0;
long prevMediaBytes = 0;
long prevMediaPackets = 0;
long prevMediaFailures = 0;
long prevPacingWaitUs = 0;
long prevSubmitted = 0;
long prevCapReal = 0;
long prevCapPointer = 0;
long prevCapTimeouts = 0;
long prevCapAccum = 0;

// Headless suppresses the per-second console [stats] line, so accumulate a compact bounded
// summary and emit it to the app log once every 10 s — the only stream telemetry a headless
// field log otherwise has. Consistent with v0.5.2 (which suppressed the unbounded 1 Hz line).
int win10Ticks = 0;
int appDiagTicks = 0;
long win10SubmitFps = 0, win10EncFps = 0, win10SentFps = 0, win10SentKbps = 0, win10Idr = 0;
long win10Re = 0, win10Po = 0, win10To = 0, win10Pres = 0;
int win10MaxDropped = 0;

void ResetWindow10()
{
    win10Ticks = 0;
    win10SubmitFps = win10EncFps = win10SentFps = win10SentKbps = win10Idr = 0;
    win10Re = win10Po = win10To = win10Pres = 0;
    win10MaxDropped = 0;
}

try
{
    while (!shutdown.IsCancellationRequested)
    {
        await Task.Delay(1000, shutdown.Token);

        var session = control.Current;
        if (session is { Streaming: true })
        {
            long enc = session.EncodedFrames;
            long idr = session.IdrRequestTotal;
            long fps = enc - prevEncoded;
            long idrDelta = idr - prevIdr;
            long audioBytes = session.AudioBytesSent;
            long audioKbps = Math.Max(0, (audioBytes - prevAudioBytes) * 8 / 1000);
            long mediaFrames = session.MediaFramesSent;
            long mediaBytes = session.MediaBytesSent;
            long mediaPackets = session.MediaPacketsSent;
            long mediaFailures = session.MediaSendFailures;
            long pacingWaitUs = session.MediaPacingWaitUs;
            long submitted = session.CaptureSubmittedFrames;
            long sentFps = mediaFrames >= prevMediaFrames ? mediaFrames - prevMediaFrames : 0;
            long submittedFps = submitted >= prevSubmitted ? submitted - prevSubmitted : 0;
            long mediaKbps = mediaBytes >= prevMediaBytes
                ? (mediaBytes - prevMediaBytes) * 8 / 1000
                : 0;
            long sentPackets = mediaPackets >= prevMediaPackets ? mediaPackets - prevMediaPackets : 0;
            long sendFailures = mediaFailures >= prevMediaFailures ? mediaFailures - prevMediaFailures : 0;
            long pacingMs = pacingWaitUs >= prevPacingWaitUs
                ? (pacingWaitUs - prevPacingWaitUs) / 1000
                : 0;

            long capReal = session.CaptureRealFrames;
            long capPointer = session.CapturePointerOnly;
            long capTimeouts = session.CaptureTimeouts;
            long capAccum = session.CaptureAccumulatedFrames;
            long realDelta = Math.Max(0, capReal - prevCapReal);
            long pointerDelta = Math.Max(0, capPointer - prevCapPointer);
            long timeoutDelta = Math.Max(0, capTimeouts - prevCapTimeouts);
            long accumDelta = Math.Max(0, capAccum - prevCapAccum);
            // DXGI AccumulatedFrames already includes the acquired present. Adding realDelta
            // double-counted a healthy 60 Hz source as ~120 presents/s and hid undersampling.
            long presentsPerSec = accumDelta;

            prevEncoded = enc;
            prevIdr = idr;
            prevAudioBytes = audioBytes;
            prevMediaFrames = mediaFrames;
            prevMediaBytes = mediaBytes;
            prevMediaPackets = mediaPackets;
            prevMediaFailures = mediaFailures;
            prevPacingWaitUs = pacingWaitUs;
            prevSubmitted = submitted;
            prevCapReal = capReal;
            prevCapPointer = capPointer;
            prevCapTimeouts = capTimeouts;
            prevCapAccum = capAccum;

            string audioStatus = session.AudioStreaming
                ? $"audio {audioKbps,4} kbps"
                : "audio off";
            string gamepadStatus = session.GamepadCount > 0
                ? $"gamepads {session.GamepadCount}"
                : "gamepads off";

            bool clientStatsFresh = session.LastClientStatsAgeMs < 3000;
            int clientInterval = Math.Max(1, session.LastClientStatsIntervalMs);
            long clientDecodedFps = clientStatsFresh
                ? RatePerSecond(session.LastClientFramesOk, clientInterval)
                : -1;
            long clientAssembledFps = clientStatsFresh && session.LastClientFramesAssembled >= 0
                ? RatePerSecond(session.LastClientFramesAssembled, clientInterval)
                : -1;
            long clientPacketRate = clientStatsFresh && session.LastClientVideoPackets >= 0
                ? RatePerSecond(session.LastClientVideoPackets, clientInterval)
                : -1;
            long clientFecPacketRate = clientStatsFresh && session.LastClientFecPackets >= 0
                ? RatePerSecond(session.LastClientFecPackets, clientInterval)
                : -1;
            int clientTransportP95Ms = session.LastCaptureToReceiveP95Ms;
            if (clientTransportP95Ms >= 0 && session.LastServerPipelineP95Ms >= 0)
                clientTransportP95Ms = Math.Max(
                    0,
                    clientTransportP95Ms - session.LastServerPipelineP95Ms);
            string bottleneck = DiagnoseBottleneck(
                session.Fps, presentsPerSec, submittedFps, fps, sentFps,
                clientStatsFresh, clientAssembledFps, clientDecodedFps,
                session.LastClientAssemblyDrops, session.LastClientDecoderDrops,
                session.LastClientFecRecovered, clientTransportP95Ms,
                session.LastDecodeToSurfaceP95Ms,
                session.MediaEndpointReady);
            string diagnosticLine =
                $"[diag] likely={bottleneck} | host {presentsPerSec} present -> " +
                $"{submittedFps} submit -> {fps} encode -> {sentFps} send | " +
                $"android {Metric(clientAssembledFps)} assemble -> {Metric(clientDecodedFps)} render | " +
                $"drops asm {Metric(session.LastClientAssemblyDrops)} dec {Metric(session.LastClientDecoderDrops)} " +
                $"fec-fix {Metric(session.LastClientFecRecovered)} | " +
                $"udp pkts tx {sentPackets}/s rx {Metric(clientPacketRate)}/s fec {Metric(clientFecPacketRate)}/s " +
                $"err {sendFailures} pace {pacingMs}ms " +
                $"@{session.MediaPacingBytesPerSec * 8 / 1_000_000}/" +
                $"{session.MediaKeyframePacingBytesPerSec * 8 / 1_000_000}Mbps/" +
                $"{session.MediaPacingBucketBytes / 1024}KiB | " +
                $"rate {session.CurrentBitrateKbps}/{session.BitrateCeilingKbps} kbps " +
                $"hold {(session.AdaptationHoldRemainingMs + 999) / 1000}s " +
                $"probe {(session.UpwardAdaptationEnabled ? "on" : "off")} | " +
                $"p95 pipe/cap/net/dec {Metric(session.LastServerPipelineP95Ms)}/" +
                $"{Metric(session.LastCaptureToReceiveP95Ms)}/{Metric(clientTransportP95Ms)}/" +
                $"{Metric(session.LastDecodeToSurfaceP95Ms)}ms";

            if (!headless)
            {
                Console.WriteLine(
                    $"[stats] {fps,3} fps encoded | {session.CurrentBitrateKbps,6} kbps | " +
                    $"media {(session.MediaEndpointReady ? "ready" : "WAIT"),5} {sentFps,3} fps/{mediaKbps,5} kbps | " +
                    $"client dropped {session.LastClientFramesDropped,3} | IDR req/s {idrDelta} | " +
                    $"cap {realDelta}re/{pointerDelta}po/{timeoutDelta}to ~{presentsPerSec}pres | " +
                    $"{audioStatus} | {gamepadStatus}");
                Console.WriteLine(diagnosticLine);
                // GUI/non-headless runs previously wrote diagnostics only to the console, so the
                // collected deskstream.app.log lacked the evidence needed for field debugging.
                if (++appDiagTicks >= 10)
                {
                    AsyncLogger.Info(diagnosticLine);
                    appDiagTicks = 0;
                }
            }
            else
            {
                // Accumulate a bounded 10 s summary for the headless app log.
                win10Ticks++;
                win10SubmitFps += submittedFps;
                win10EncFps += fps;
                win10SentFps += sentFps;
                win10SentKbps += mediaKbps;
                win10Idr += idrDelta;
                win10Re += realDelta;
                win10Po += pointerDelta;
                win10To += timeoutDelta;
                win10Pres += presentsPerSec;
                win10MaxDropped = Math.Max(win10MaxDropped, session.LastClientFramesDropped);
                if (win10Ticks >= 10)
                {
                    AsyncLogger.Info(
                        $"[stats10s] host ~{win10SubmitFps / win10Ticks} submit -> " +
                        $"{win10EncFps / win10Ticks} encode -> {win10SentFps / win10Ticks} send fps | " +
                        $"android {Metric(clientAssembledFps)} assemble -> {Metric(clientDecodedFps)} render fps | " +
                        $"sent ~{win10SentKbps / win10Ticks} kbps @ " +
                        $"{session.CurrentBitrateKbps}/{session.BitrateCeilingKbps} kbps " +
                        $"hold {(session.AdaptationHoldRemainingMs + 999) / 1000}s " +
                        $"probe {(session.UpwardAdaptationEnabled ? "on" : "off")} | " +
                        $"drops asm {Metric(session.LastClientAssemblyDrops)} dec {Metric(session.LastClientDecoderDrops)} " +
                        $"fec-fix {Metric(session.LastClientFecRecovered)} total-max {win10MaxDropped} | " +
                        $"udp last tx {sentPackets}/s rx {Metric(clientPacketRate)}/s err {sendFailures} " +
                        $"pace {pacingMs}ms @{session.MediaPacingBytesPerSec * 8 / 1_000_000}/" +
                        $"{session.MediaKeyframePacingBytesPerSec * 8 / 1_000_000}Mbps/" +
                        $"{session.MediaPacingBucketBytes / 1024}KiB | IDR req {win10Idr} | " +
                        $"cap {win10Re}re/{win10Po}po/{win10To}to ~{win10Pres}pres | " +
                        $"likely {bottleneck} p95 pipe/cap/net/dec " +
                        $"{Metric(session.LastServerPipelineP95Ms)}/" +
                        $"{Metric(session.LastCaptureToReceiveP95Ms)}/" +
                        $"{Metric(clientTransportP95Ms)}/" +
                        $"{Metric(session.LastDecodeToSurfaceP95Ms)}ms");
                    ResetWindow10();
                }
            }
        }
        else
        {
            ResetWindow10();
            appDiagTicks = 0;
            prevEncoded = 0;
            prevIdr = 0;
            prevAudioBytes = 0;
            prevMediaFrames = 0;
            prevMediaBytes = 0;
            prevMediaPackets = 0;
            prevMediaFailures = 0;
            prevPacingWaitUs = 0;
            prevSubmitted = 0;
            prevCapReal = 0;
            prevCapPointer = 0;
            prevCapTimeouts = 0;
            prevCapAccum = 0;
        }
    }
}
catch (OperationCanceledException)
{
    // graceful shutdown
}

Console.WriteLine("Shutting down...");
AsyncLogger.Info("Server shutting down.");
if (OperatingSystem.IsWindows())
{
    try { NativeMethods.TimeEndPeriod(1); } catch { }
}
return 0;

static long RatePerSecond(int count, int intervalMs) =>
    intervalMs > 0 ? Math.Max(0, count) * 1000L / intervalMs : -1;

static string Metric(long value) => value < 0 ? "-" : value.ToString();

static string DiagnoseBottleneck(
    int targetFps,
    long presents,
    long submitted,
    long encoded,
    long sent,
    bool clientFresh,
    long assembled,
    long decoded,
    int assemblyDrops,
    int decoderDrops,
    int fecRecovered,
    int networkP95Ms,
    int decoderP95Ms,
    bool endpointReady)
{
    if (!endpointReady) return "media-endpoint";
    long low = Math.Max(1, targetFps * 85L / 100L);

    // Follow the pipeline in order. The first stage that falls materially below target is the
    // most useful place to investigate; later stages cannot exceed its input rate.
    if (presents < low) return "host-present-rate";
    if (submitted < low) return "server-capture/convert";
    if (encoded < Math.Min(low, submitted - 2)) return "server-encoder";
    if (sent < Math.Min(low, encoded - 2)) return "server-udp-send";
    if (!clientFresh) return "client-stats-missing";

    if (assemblyDrops > 0 || fecRecovered > 2 ||
        (assembled >= 0 && sent >= low && assembled + 3 < sent))
        return "wifi/udp-loss";
    if (decoderDrops > 0 ||
        (assembled >= 0 && decoded >= 0 && decoded + 3 < assembled) ||
        decoderP95Ms > 50)
        return "android-decoder";
    if (networkP95Ms > 80)
        return "wifi-jitter";
    if (decoded >= low) return "healthy";
    return "client/network-underrun";
}

static IEnumerable<string> LocalIPv4Addresses()
{
    var result = new List<string>();
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up)
            continue;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            continue;

        foreach (var addr in ni.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                result.Add(addr.Address.ToString());
        }
    }

    if (result.Count == 0)
        result.Add("(no active IPv4 interface found)");
    return result;
}

internal static class NativeMethods
{
    public const uint HIGH_PRIORITY_CLASS = 0x00000080;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPriorityClass(IntPtr handle, uint priorityClass);

    // MMCSS: register a thread with a pro-audio/games multimedia scheduling class so the
    // capture and encoder threads keep getting CPU under 100% GPU/CPU game load.
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    /// <summary>
    /// Registers the calling thread with the MMCSS "Games" task. Returns an opaque handle to pass
    /// to <see cref="RevertGamesMmcss"/> on thread exit, or IntPtr.Zero if unavailable. Never throws.
    /// </summary>
    public static IntPtr JoinGamesMmcss()
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;
        try
        {
            uint taskIndex = 0;
            return AvSetMmThreadCharacteristicsW("Games", ref taskIndex);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Reverts an MMCSS registration obtained from <see cref="JoinGamesMmcss"/>. Never throws.</summary>
    public static void RevertGamesMmcss(IntPtr avrtHandle)
    {
        if (avrtHandle == IntPtr.Zero)
            return;
        try { AvRevertMmThreadCharacteristics(avrtHandle); } catch { }
    }
}
