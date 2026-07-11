using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DeskStreamer.Server.Protocol;
using DeskStreamer.Server.Session;

namespace DeskStreamer.Server.Net;

/// <summary>
/// Control channel (PROTOCOL.md §2): TCP 47801, length-prefixed (uint32 BE) UTF-8 JSON.
/// Handles framing, keepalive (PING/PONG + 6 s dead-connection timeout), and single-client
/// enforcement. Protocol semantics live in <see cref="StreamSession"/>.
/// </summary>
public sealed class ControlServer : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(6);

    private readonly PairingManager _pairing;
    private readonly string _serverName;
    private readonly ServerOptions _options;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _busy;
    private StreamSession? _current;

    /// <summary>The active session, if any (read by the console stats printer and web dashboard).</summary>
    public StreamSession? Current => Volatile.Read(ref _current);

    public ControlServer(PairingManager pairing, string serverName, ServerOptions options)
    {
        _pairing = pairing;
        _serverName = serverName;
        _options = options;
        _listener = new TcpListener(IPAddress.Any, Ports.Control);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 1)
            {
                // A client is already connected — reject the second per PROTOCOL.md.
                _ = RejectAsync(tcp);
                continue;
            }

            _ = HandleClientAsync(tcp, ct)
                .ContinueWith(_ => Interlocked.Exchange(ref _busy, 0), TaskScheduler.Default);
        }
    }

    private async Task RejectAsync(TcpClient tcp)
    {
        try
        {
            using (tcp)
            {
                var stream = tcp.GetStream();
                await WriteFrameAsync(stream, OutgoingMessages.Error("BUSY", "another client is connected"));
            }
        }
        catch { /* ignore */ }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var remote = tcp.Client.RemoteEndPoint;
        Console.WriteLine($"[control] client connected from {remote}.");

        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        var sendGate = new object();

        void Send(object message)
        {
            lock (sendGate)
            {
                try { WriteFrameSync(stream, message); } catch { /* socket dying; loop will end */ }
            }
        }

        var clientAddress = (remote as IPEndPoint)?.Address;
        var session = new StreamSession(
            Send,
            () => { try { tcp.Close(); } catch { } },
            _pairing,
            _serverName,
            clientAddress,
            _options);
        Volatile.Write(ref _current, session);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame = await ReadFrameAsync(stream, ct);
                if (frame == null)
                    break; // clean close or timeout

                // Drain any dashboard commands on this (control) thread before handling the
                // frame, so session start/stop stays serialized with control-message handling.
                // Bounded latency: the client PINGs every 2 s, so a queued command runs within
                // one keepalive interval even on an otherwise idle control channel.
                session.DrainCommands();

                string? type = ReadType(frame);
                if (type == null)
                {
                    // Malformed frame: either side closes the socket (PROTOCOL.md §2).
                    break;
                }

                if (type == "PING")
                {
                    long t1Us = MonotonicClock.NowUs;
                    long? t0Us = ReadOptionalInt64(frame, "t0Us");
                    Send(OutgoingMessages.Pong(t0Us, t0Us.HasValue ? t1Us : null,
                        t0Us.HasValue ? MonotonicClock.NowUs : null));
                    continue;
                }

                session.HandleMessage(type, frame);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[control] connection error: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _current, null);
            session.Dispose();
            try { tcp.Close(); } catch { }
            Console.WriteLine($"[control] client {remote} disconnected.");
        }
    }

    // ---- Framing --------------------------------------------------------------------------

    private static string? ReadType(byte[] frame)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static long? ReadOptionalInt64(byte[] frame, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            if (doc.RootElement.TryGetProperty(name, out var value) && value.TryGetInt64(out long result))
                return result;
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(stream, lenBuf, 4, ct))
            return null;

        uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
        if (len == 0 || len > ProtocolConstants.MaxControlFrame)
            return null; // malformed

        var payload = new byte[len];
        if (!await ReadExactAsync(stream, payload, (int)len, ct))
            return null;

        return payload;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, honoring the 6 s idle timeout. Returns
    /// false on EOF, timeout, or cancellation.
    /// </summary>
    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(IdleTimeout);
            try
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), timeoutCts.Token);
                if (n == 0)
                    return false; // peer closed
                read += n;
            }
            catch (OperationCanceledException)
            {
                return false; // idle timeout or shutdown
            }
            catch (IOException)
            {
                return false;
            }
        }
        return true;
    }

    private static void WriteFrameSync(NetworkStream stream, object message)
    {
        byte[] json = Json.SerializeToUtf8(message);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)json.Length);
        stream.Write(header);
        stream.Write(json);
        stream.Flush();
    }

    private static async Task WriteFrameAsync(NetworkStream stream, object message)
    {
        byte[] json = Json.SerializeToUtf8(message);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)json.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(json);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _acceptLoop?.Wait(1000); } catch { }
        _cts?.Dispose();
    }
}
