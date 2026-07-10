using System.Net;
using System.Net.Sockets;
using System.Text;
using DeskStreamer.Server.Protocol;

namespace DeskStreamer.Server.Net;

/// <summary>
/// Discovery responder (PROTOCOL.md §1). Listens on UDP 47800 for the 8 ASCII bytes
/// "DSPROBE1" and replies unicast to the sender with a single DSREPLY JSON datagram.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    private static readonly byte[] Probe = Encoding.ASCII.GetBytes("DSPROBE1");

    private readonly string _hostname;
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DiscoveryResponder(string hostname) => _hostname = hostname;

    public void Start()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Allow other tools / re-binds; broadcast reception needs the reuse flag on some setups.
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.EnableBroadcast = true;
        _socket.Bind(new IPEndPoint(IPAddress.Any, Ports.Discovery));

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[64];
        var reply = Json.SerializeToUtf8(OutgoingMessages.DiscoveryReply(_hostname));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _socket!.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);

                if (result.ReceivedBytes == Probe.Length &&
                    buffer.AsSpan(0, Probe.Length).SequenceEqual(Probe))
                {
                    // Unicast reply back to the probing client.
                    await _socket.SendToAsync(reply, SocketFlags.None, result.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // Transient; keep serving.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _socket?.Dispose(); } catch { }
        try { _loop?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}
