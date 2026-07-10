using System.Net;
using System.Net.Sockets;
using System.Text;
using DeskStreamer.Server.Protocol;

namespace DeskStreamer.Server.Net;

/// <summary>
/// Media transport (PROTOCOL.md §3). Splits each encoded H.264 access unit into
/// &lt;=1200-byte chunks, generates XOR FEC parity (groups of 8), and unicasts each
/// datagram to the client address learned from "DSMH" hole-punch packets.
///
/// Hot path is allocation-free: a single reusable send buffer and one reusable FEC
/// accumulator. SendFrame is only ever called from the single encoder callback thread.
/// </summary>
public sealed class MediaSender : IDisposable
{
    private const int FecGroup = 8;
    private static readonly byte[] Dsmh = Encoding.ASCII.GetBytes("DSMH");

    private readonly Socket _socket;
    private readonly byte[] _sendBuffer = new byte[MediaPacket.MaxDatagram];
    private readonly byte[] _fecBuffer = new byte[MediaPacket.MaxPayload];

    private volatile EndPoint? _clientEndpoint;
    private CancellationTokenSource? _cts;
    private Task? _learnLoop;

    public int Port { get; }
    public bool HasClient => _clientEndpoint != null;

    public MediaSender(int preferredPort)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // Keep the OS send buffer small: dropping beats queuing for latency (PROTOCOL.md §3.3).
        try { _socket.SendBufferSize = 256 * 1024; } catch { }

        // DSCP AF41 / TOS 0x88, best-effort (PROTOCOL.md §3.3). IP_TOS = 3; Windows usually
        // ignores this without qWAVE, hence best-effort. May throw without privilege.
        try { _socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)3, 0x88); }
        catch { }

        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, preferredPort));
            Port = preferredPort;
        }
        catch (SocketException)
        {
            // Preferred port taken — fall back to an ephemeral port and report the real one.
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }
    }

    /// <summary>Starts the background loop that learns the client's media address from DSMH packets.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _learnLoop = Task.Run(() => LearnLoopAsync(_cts.Token));
    }

    private async Task LearnLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _socket.ReceiveFromAsync(
                    buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);

                if (r.ReceivedBytes >= Dsmh.Length &&
                    buf.AsSpan(0, Dsmh.Length).SequenceEqual(Dsmh))
                {
                    // The server sends all media to the source of the most recent DSMH.
                    _clientEndpoint = r.RemoteEndPoint;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { /* ignore, keep learning */ }
        }
    }

    /// <summary>
    /// Packetizes and sends one encoded access unit. No-op until a client address is known.
    /// </summary>
    /// <param name="au">Buffer holding the Annex-B access unit (may be larger than length).</param>
    /// <param name="length">Valid bytes in <paramref name="au"/>.</param>
    /// <param name="frameId">Monotonic frame id.</param>
    /// <param name="keyframe">True if this AU is an IDR (carries SPS/PPS).</param>
    /// <param name="ptsMs">Server steady-clock ms (stats only).</param>
    public void SendFrame(byte[] au, int length, uint frameId, bool keyframe, uint ptsMs)
    {
        var client = _clientEndpoint;
        if (client == null || length <= 0)
            return;

        int packetCount = (length + MediaPacket.MaxPayload - 1) / MediaPacket.MaxPayload;
        if (packetCount > ushort.MaxValue)
            return; // absurdly large frame; skip rather than corrupt indices

        int fecCount = (packetCount + FecGroup - 1) / FecGroup;
        byte dataFlags = keyframe ? MediaPacket.FlagKeyframe : (byte)0;

        // ---- DATA packets ----
        for (int i = 0; i < packetCount; i++)
        {
            int offset = i * MediaPacket.MaxPayload;
            int payloadLen = (i == packetCount - 1) ? (length - offset) : MediaPacket.MaxPayload;

            MediaPacket.WriteHeader(
                _sendBuffer, dataFlags, (ushort)payloadLen, frameId,
                (ushort)i, (ushort)packetCount, (ushort)fecCount, ptsMs);

            Buffer.BlockCopy(au, offset, _sendBuffer, MediaPacket.HeaderSize, payloadLen);
            SendDatagram(client, MediaPacket.HeaderSize + payloadLen);
        }

        // ---- FEC packets: one XOR parity per group of up to 8 data packets ----
        for (int g = 0; g < fecCount; g++)
        {
            int first = g * FecGroup;
            int last = Math.Min(first + FecGroup, packetCount) - 1;

            // Group's longest payload: 1200 unless this group is only the final (remainder) packet.
            int maxLen = (last == packetCount - 1 && first == last)
                ? (length - first * MediaPacket.MaxPayload)
                : MediaPacket.MaxPayload;

            Array.Clear(_fecBuffer, 0, maxLen);
            for (int i = first; i <= last; i++)
            {
                int offset = i * MediaPacket.MaxPayload;
                int payloadLen = (i == packetCount - 1) ? (length - offset) : MediaPacket.MaxPayload;
                for (int b = 0; b < payloadLen; b++)
                    _fecBuffer[b] ^= au[offset + b];
                // bytes [payloadLen, maxLen) contribute zero (zero-padding) — nothing to XOR.
            }

            byte fecFlags = (byte)(dataFlags | MediaPacket.FlagFec);
            MediaPacket.WriteHeader(
                _sendBuffer, fecFlags, (ushort)maxLen, frameId,
                (ushort)g, (ushort)packetCount, (ushort)fecCount, ptsMs);

            Buffer.BlockCopy(_fecBuffer, 0, _sendBuffer, MediaPacket.HeaderSize, maxLen);
            SendDatagram(client, MediaPacket.HeaderSize + maxLen);
        }
    }

    private void SendDatagram(EndPoint client, int totalLen)
    {
        try
        {
            _socket.SendTo(_sendBuffer, 0, totalLen, SocketFlags.None, client);
        }
        catch (SocketException) { /* drop on transient send errors — never block the pipeline */ }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _socket.Dispose(); } catch { }
        try { _learnLoop?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}
