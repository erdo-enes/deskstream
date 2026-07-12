using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeskStreamer.Server.Protocol;

namespace DeskStreamer.Server.Net;

/// <summary>
/// Media transport (PROTOCOL.md §3). Splits each encoded H.264 access unit into
/// &lt;=1200-byte chunks, generates interleaved XOR FEC parity, and unicasts each
/// datagram to the client address learned from "DSMH" hole-punch packets.
///
/// Hot path is allocation-free: a single reusable send buffer and one reusable FEC
/// accumulator. SendFrame is only ever called from the single encoder callback thread.
/// </summary>
public sealed class MediaSender : IDisposable
{
    // Interleaved XOR FEC: data packets are spread across FecInterleave groups so that a burst
    // loss of up to FecInterleave consecutive packets hits different groups (1 loss each → all
    // recoverable). With the old consecutive groups of 8, a 2-packet burst in one group killed
    // the frame. This remains simple XOR protection, not a substitute for general erasure coding.
    private const int FecInterleave = 4;
    private static readonly byte[] Dsmh = Encoding.ASCII.GetBytes("DSMH");
    private static readonly byte[] Dshb = Encoding.ASCII.GetBytes("DSHB");

    private readonly Socket _socket;
    private readonly IPAddress? _expectedClientAddress;
    private readonly byte[] _sendBuffer = new byte[MediaPacket.MaxDatagram];
    private readonly byte[] _fecBuffer = new byte[MediaPacket.MaxPayload];

    private volatile EndPoint? _clientEndpoint;
    private CancellationTokenSource? _cts;
    private Task? _learnLoop;
    private Task? _heartbeatLoop;
    private long _framesSent;
    private long _packetsSent;
    private long _bytesSent;
    private long _sendFailures;
    private long _pacingWaitUs;

    // Token-bucket micro-pacing (PROTOCOL.md §3.3). Only oversized IDR bursts get shaped: the
    // bucket comfortably covers a normal P-frame, so those pass untouched. Shaping a ~150-300 KB
    // IDR at the 96 Mbps ceiling adds a one-frame ~12-25 ms spike instead of a 2x/s burst that
    // makes a Wi-Fi client drop 3-8 packets and slash bitrate. _pacingBytesPerSec and
    // _bucketCapacity are set from the control thread (SetPacingRate) and read on the encoder
    // send thread; the token count + refill timestamp are touched on the send thread only.
    // (C# forbids `volatile long`, so cross-thread access uses Volatile.Read/Write.)
    private long _pacingBytesPerSec;   // 0 = pacing disabled
    private long _bucketCapacity;
    private double _tokens;
    private long _lastRefillTs;

    /// <summary>
    /// Configures the pacing rate from the live encoder target. avg = the CBR byte rate; the pacing
    /// rate is 8x that, clamped to [32 Mbps, 96 Mbps] (floor keeps P-frames instant; ceiling stays
    /// under a single 5 GHz client's drain rate). Bucket capacity is two frames' worth of average
    /// bytes (min 64 KiB) so a normal frame never trips the bucket. Call at pipeline start and on
    /// every bitrate change; 0 fps or kbps disables pacing.
    /// </summary>
    public void SetPacingRate(int currentBitrateKbps, int fps)
    {
        if (currentBitrateKbps <= 0 || fps <= 0)
        {
            Volatile.Write(ref _pacingBytesPerSec, 0);
            return;
        }
        long avg = (long)currentBitrateKbps * 1000 / 8;          // bytes/sec at the CBR target
        long rate = Math.Clamp(avg * 8, 4_000_000, 12_000_000);   // 32..96 Mbps in bytes/sec
        long capacity = Math.Max(64 * 1024, avg / fps * 2);
        Volatile.Write(ref _bucketCapacity, capacity);
        Volatile.Write(ref _pacingBytesPerSec, rate);
    }

    /// <summary>
    /// Token-bucket gate run on the send thread at the top of the datagram-send routine. Refills by
    /// elapsed*rate (capped at capacity); if the datagram fits, subtract and return immediately;
    /// otherwise coarse-sleep then spin to the exact target tick and drain the bucket to zero.
    /// </summary>
    private void PaceBeforeSend(int len)
    {
        long rate = Volatile.Read(ref _pacingBytesPerSec);
        if (rate <= 0)
            return; // pacing disabled: never touch the hot path

        long capacity = Volatile.Read(ref _bucketCapacity);
        if (capacity <= 0)
            capacity = 64 * 1024;

        long now = Stopwatch.GetTimestamp();
        double elapsedSec = (now - _lastRefillTs) / (double)Stopwatch.Frequency;
        _lastRefillTs = now;
        _tokens = Math.Min(capacity, _tokens + elapsedSec * rate);

        _tokens -= len;
        if (_tokens >= 0)
            return;

        // Preserve sub-millisecond debt across datagrams. Resetting the bucket to zero here made
        // every ~0.1 ms wait disappear, so a large IDR still left as one unpaced Wi-Fi burst.
        // Sleeping only after debt reaches 1 ms keeps CPU use low without imposing 1 ms/packet.
        long waitUs = (long)Math.Ceiling(-_tokens * 1_000_000.0 / rate);
        if (waitUs < 1000)
            return;

        long beforeSleep = Stopwatch.GetTimestamp();
        Thread.Sleep((int)Math.Ceiling(waitUs / 1000.0));
        long afterSleep = Stopwatch.GetTimestamp();
        long actualWaitUs = (afterSleep - beforeSleep) * 1_000_000 / Stopwatch.Frequency;
        Interlocked.Add(ref _pacingWaitUs, actualWaitUs);

        // Account for the actual sleep now so the next datagram does not double-count it.
        _tokens = Math.Min(capacity, _tokens + actualWaitUs * rate / 1_000_000.0);
        _lastRefillTs = afterSleep;
    }

    /// <summary>Validated gamepad snapshots from the currently learned media endpoint.</summary>
    public Action<GamepadState>? OnGamepadState { get; set; }
    public Action<MouseMotion>? OnMouseMotion { get; set; }
    /// <summary>Raised once when a fresh client media endpoint is learned from DSMH.</summary>
    public Action? OnClientConnected { get; set; }

    public int Port { get; }
    public bool HasClient => _clientEndpoint != null;
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long SendFailures => Interlocked.Read(ref _sendFailures);
    public long PacingWaitUs => Interlocked.Read(ref _pacingWaitUs);

    public MediaSender(int preferredPort, IPAddress? expectedClientAddress = null)
    {
        _expectedClientAddress = expectedClientAddress;
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
        // Keep the default blocking mode. Non-blocking Windows UDP commonly returns
        // WSAEWOULDBLOCK during IDR bursts; abandoning the remainder then guarantees an
        // incomplete frame. The bounded kernel buffer still limits queue growth.
    }

    /// <summary>Starts the background loop that learns the client's media address from DSMH packets.</summary>
    public void Start()
    {
        _tokens = Volatile.Read(ref _bucketCapacity);
        _lastRefillTs = Stopwatch.GetTimestamp();
        _cts = new CancellationTokenSource();
        _learnLoop = Task.Run(() => LearnLoopAsync(_cts.Token));
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Learns the Android media port over the already-authenticated TCP channel. UDP DSMH
    /// remains a fallback, but some Wi-Fi/router combinations drop client-to-server UDP while
    /// still allowing server-to-client media.
    /// </summary>
    public bool SetClientPort(int port)
    {
        if (_expectedClientAddress == null || port is < 1 or > 65535)
            return false;
        SetClientEndpoint(new IPEndPoint(_expectedClientAddress, port));
        return true;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
            var client = _clientEndpoint;
            if (client == null) continue;
            try { _socket.SendTo(Dshb, client); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { break; }
        }
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

                if (_expectedClientAddress != null &&
                    r.RemoteEndPoint is IPEndPoint remote &&
                    !remote.Address.Equals(_expectedClientAddress))
                {
                    continue;
                }

                if (r.ReceivedBytes >= Dsmh.Length &&
                    buf.AsSpan(0, Dsmh.Length).SequenceEqual(Dsmh))
                {
                    // The server sends all media to the source of the most recent DSMH.
                    SetClientEndpoint(r.RemoteEndPoint);
                }
                else if (_clientEndpoint?.Equals(r.RemoteEndPoint) == true &&
                         GamepadPacket.TryParse(buf.AsSpan(0, r.ReceivedBytes), out var state))
                {
                    OnGamepadState?.Invoke(state);
                }
                else if (_clientEndpoint?.Equals(r.RemoteEndPoint) == true &&
                         MousePacket.TryParse(buf.AsSpan(0, r.ReceivedBytes), out var motion))
                {
                    OnMouseMotion?.Invoke(motion);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { /* ignore, keep learning */ }
        }
    }

    private void SetClientEndpoint(EndPoint endpoint)
    {
        bool endpointChanged = _clientEndpoint?.Equals(endpoint) != true;
        _clientEndpoint = endpoint;
        if (endpointChanged)
            OnClientConnected?.Invoke();
    }

    /// <summary>
    /// Packetizes and sends one encoded access unit. No-op until a client address is known.
    /// </summary>
    /// <param name="au">Buffer holding the Annex-B access unit (may be larger than length).</param>
    /// <param name="length">Valid bytes in <paramref name="au"/>.</param>
    /// <param name="frameId">Monotonic frame id.</param>
    /// <param name="keyframe">True if this AU is an IDR (carries SPS/PPS).</param>
    /// <param name="ptsMs">Server steady-clock ms (stats only).</param>
    public void SendFrame(
        byte[] au, int length, uint frameId, bool keyframe, uint ptsMs, ushort pipelineDelayMs)
    {
        var client = _clientEndpoint;
        if (client == null || length <= 0)
            return;

        int packetCount = (length + MediaPacket.MaxPayload - 1) / MediaPacket.MaxPayload;
        if (packetCount > ushort.MaxValue)
            return; // absurdly large frame; skip rather than corrupt indices

        int fecCount = Math.Min(FecInterleave, packetCount);
        byte dataFlags = keyframe ? MediaPacket.FlagKeyframe : (byte)0;
        bool allDataSent = true;

        // ---- DATA packets ----
        for (int i = 0; i < packetCount; i++)
        {
            int offset = i * MediaPacket.MaxPayload;
            int payloadLen = (i == packetCount - 1) ? (length - offset) : MediaPacket.MaxPayload;

            MediaPacket.WriteHeader(
                _sendBuffer, dataFlags, (ushort)payloadLen, frameId,
                (ushort)i, (ushort)packetCount, (ushort)fecCount, ptsMs, pipelineDelayMs);

            Buffer.BlockCopy(au, offset, _sendBuffer, MediaPacket.HeaderSize, payloadLen);
            if (!SendDatagram(client, MediaPacket.HeaderSize + payloadLen))
                allDataSent = false;
        }

        // ---- FEC packets: interleaved XOR parity ----
        // Group g contains data packets at indices g, g+FecInterleave, g+2*FecInterleave, ...
        // A burst loss of up to FecInterleave consecutive packets hits each group at most once.
        for (int g = 0; g < fecCount; g++)
        {
            // Parity must be as long as the largest group member. The old code started at 1200
            // and then shortened the group when it encountered the final partial packet, even if
            // that group also contained full packets. Such parity silently corrupted recovery.
            int maxLen = 0;
            for (int i = g; i < packetCount; i += FecInterleave)
            {
                int offset = i * MediaPacket.MaxPayload;
                int payloadLen = i == packetCount - 1
                    ? length - offset
                    : MediaPacket.MaxPayload;
                maxLen = Math.Max(maxLen, payloadLen);
            }

            Array.Clear(_fecBuffer, 0, maxLen);
            for (int i = g; i < packetCount; i += FecInterleave)
            {
                int offset = i * MediaPacket.MaxPayload;
                int payloadLen = (i == packetCount - 1) ? (length - offset) : MediaPacket.MaxPayload;
                for (int b = 0; b < payloadLen; b++)
                    _fecBuffer[b] ^= au[offset + b];
            }

            byte fecFlags = (byte)(dataFlags | MediaPacket.FlagFec);
            MediaPacket.WriteHeader(
                _sendBuffer, fecFlags, (ushort)maxLen, frameId,
                (ushort)g, (ushort)packetCount, (ushort)fecCount, ptsMs, pipelineDelayMs);

            Buffer.BlockCopy(_fecBuffer, 0, _sendBuffer, MediaPacket.HeaderSize, maxLen);
            SendDatagram(client, MediaPacket.HeaderSize + maxLen);
        }

        if (allDataSent)
            Interlocked.Increment(ref _framesSent);
    }

    public void SendCursorPosition(uint sequence, CursorPosition position)
    {
        var client = _clientEndpoint;
        if (client == null) return;
        Span<byte> packet = stackalloc byte[CursorPacket.Size];
        CursorPacket.Write(packet, sequence, position);
        try { _socket.SendTo(packet, SocketFlags.None, client); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private bool SendDatagram(EndPoint client, int totalLen)
    {
        PaceBeforeSend(totalLen);
        try
        {
            int sent = _socket.SendTo(_sendBuffer, 0, totalLen, SocketFlags.None, client);
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, sent);
            return sent == totalLen;
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref _sendFailures);
            return false;
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Increment(ref _sendFailures);
            return false;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _socket.Dispose(); } catch { }
        try { _learnLoop?.Wait(1000); } catch { }
        try { _heartbeatLoop?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}
