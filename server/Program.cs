using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime;
using DeskStreamer.Server.Net;
using DeskStreamer.Server.Protocol;
using DeskStreamer.Server.Session;

// Favor latency over throughput in the GC (hot path is otherwise allocation-free).
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

string hostname = Dns.GetHostName();

Console.WriteLine("DeskStream server");
Console.WriteLine("=================");
Console.WriteLine($"Host: {hostname}");
Console.WriteLine("Listening on:");
foreach (var ip in LocalIPv4Addresses())
    Console.WriteLine($"  {ip}   (discovery UDP {Ports.Discovery}, control TCP {Ports.Control}, " +
                      $"video UDP {Ports.PreferredMedia}, audio UDP {Ports.PreferredAudio})");
Console.WriteLine();
Console.WriteLine("Firewall: allow UDP 47800/47802/47803 and TCP 47801 on the Private network profile.");
Console.WriteLine("Waiting for a client to connect...");
Console.WriteLine();

var pairing = new PairingManager();

using var discovery = new DiscoveryResponder(hostname);
using var control = new ControlServer(pairing, hostname);

discovery.Start();
control.Start();

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

            Console.WriteLine(
                $"[stats] {fps,3} fps encoded | {session.CurrentBitrateKbps,6} kbps | " +
                $"media {(session.MediaEndpointReady ? "ready" : "WAIT"),5} {sentFps,3} fps/{mediaKbps,5} kbps | " +
                $"client dropped {session.LastClientFramesDropped,3} | IDR req/s {idrDelta} | " +
                $"{audioStatus} | {gamepadStatus}");
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
return;

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
