using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.InteropServices;
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

var options = new ServerOptions
{
    DefaultQuality = ServerOptions.Normalize(FlagValue("--quality")),
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
}

// ---- Startup -----------------------------------------------------------------------------

if (OperatingSystem.IsWindows())
{
    try { NativeMethods.TimeBeginPeriod(1); } catch { }
}

// Favor latency over throughput in the GC (hot path is otherwise allocation-free).
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

string hostname = Dns.GetHostName();
var listenIps = LocalIPv4Addresses().ToList();

Console.WriteLine("DeskStream server");
Console.WriteLine("=================");
Console.WriteLine($"Host: {hostname}");
Console.WriteLine("Listening on:");
foreach (var ip in listenIps)
    Console.WriteLine($"  {ip}   (discovery UDP {Ports.Discovery}, control TCP {Ports.Control}, " +
                      $"video UDP {Ports.PreferredMedia}, audio UDP {Ports.PreferredAudio})");
Console.WriteLine();
Console.WriteLine($"Default stream quality: {options.DefaultQuality}");
Console.WriteLine($"Maximum stream bitrate: {options.MaxBitrateKbps} kbps");
string firewallPorts = "UDP 47800/47802/47803 and TCP 47801";
if (webLan && !noWeb)
    firewallPorts += $" plus TCP {webPort} for the LAN dashboard";
Console.WriteLine($"Firewall: allow {firewallPorts} on the Private network profile.");
Console.WriteLine("Waiting for a client to connect...");
Console.WriteLine();

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
            long sentFps = mediaFrames >= prevMediaFrames ? mediaFrames - prevMediaFrames : 0;
            long mediaKbps = mediaBytes >= prevMediaBytes
                ? (mediaBytes - prevMediaBytes) * 8 / 1000
                : 0;
            prevEncoded = enc;
            prevIdr = idr;
            prevAudioBytes = audioBytes;
            prevMediaFrames = mediaFrames;
            prevMediaBytes = mediaBytes;

            string audioStatus = session.AudioStreaming
                ? $"audio {audioKbps,4} kbps"
                : "audio off";
            string gamepadStatus = session.GamepadCount > 0
                ? $"gamepads {session.GamepadCount}"
                : "gamepads off";

            if (!headless)
            {
                Console.WriteLine(
                    $"[stats] {fps,3} fps encoded | {session.CurrentBitrateKbps,6} kbps | " +
                    $"media {(session.MediaEndpointReady ? "ready" : "WAIT"),5} {sentFps,3} fps/{mediaKbps,5} kbps | " +
                    $"client dropped {session.LastClientFramesDropped,3} | IDR req/s {idrDelta} | " +
                    $"{audioStatus} | {gamepadStatus}");
            }
        }
        else
        {
            prevEncoded = 0;
            prevIdr = 0;
            prevAudioBytes = 0;
            prevMediaFrames = 0;
            prevMediaBytes = 0;
        }
    }
}
catch (OperationCanceledException)
{
    // graceful shutdown
}

Console.WriteLine("Shutting down...");
if (OperatingSystem.IsWindows())
{
    try { NativeMethods.TimeEndPeriod(1); } catch { }
}
return 0;

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
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uPeriod);
}
