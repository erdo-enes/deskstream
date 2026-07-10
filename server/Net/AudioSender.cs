using System.Net;
using System.Net.Sockets;
using System.Text;
using DeskStreamer.Server.Protocol;

namespace DeskStreamer.Server.Net;

/// <summary>
/// Dedicated UDP audio transport (PROTOCOL.md §3A). Captured PCM is grouped into fixed
/// 5 ms blocks and sent immediately to the endpoint learned from DSAH hole punches.
/// </summary>
public sealed class AudioSender : IDisposable
{
    private static readonly byte[] Dsah = Encoding.ASCII.GetBytes("DSAH");

    private readonly Socket _socket;
    private readonly IPAddress? _expectedClientAddress;
    private readonly byte[] _datagram = new byte[AudioPacket.DatagramSize];
    private readonly byte[] _partial = new byte[AudioPacket.PayloadSize];
    private int _partialLength;
    private uint _partialPtsMs;
    private uint _sequence;
    private volatile EndPoint? _clientEndpoint;
    private CancellationTokenSource? _cts;
    private Task? _learnLoop;
    private long _packetsSent;
    private long _bytesSent;

    public int Port { get; }
    public bool HasClient => _clientEndpoint != null;
    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public AudioSender(int preferredPort, IPAddress? expectedClientAddress = null)
    {
        _expectedClientAddress = expectedClientAddress;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { _socket.SendBufferSize = 64 * 1024; } catch { }
        try { _socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)3, 0x88); } catch { }

        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, preferredPort));
            Port = preferredPort;
        }
        catch (SocketException)
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _learnLoop = Task.Run(() => LearnLoopAsync(_cts.Token));
    }

    /// <summary>Authenticated TCP fallback for networks that block DSAH UDP punches.</summary>
    public bool SetClientPort(int port)
    {
        if (_expectedClientAddress == null || port is < 1 or > 65535)
            return false;
        _clientEndpoint = new IPEndPoint(_expectedClientAddress, port);
        return true;
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
                if (r.ReceivedBytes >= Dsah.Length &&
                    buf.AsSpan(0, Dsah.Length).SequenceEqual(Dsah))
                {
                    _clientEndpoint = r.RemoteEndPoint;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { }
        }
    }

    /// <summary>
    /// Accepts aligned PCM16 stereo bytes from the single WASAPI callback thread. One
    /// partial 5 ms block is retained; there is no queue and no allocation per packet.
    /// </summary>
    public void SendPcm(byte[] pcm, int length, uint captureEndPtsMs)
    {
        length -= length % AudioPacket.BytesPerFrame;
        if (length <= 0)
            return;

        int totalFrames = length / AudioPacket.BytesPerFrame;
        long capturedDurationMs = (long)totalFrames * 1000 / AudioPacket.SampleRate;
        uint inputStartPtsMs = (uint)Math.Max(0, (long)captureEndPtsMs - capturedDurationMs);
        int sourceOffset = 0;

        while (sourceOffset < length)
        {
            if (_partialLength == 0)
            {
                int consumedFrames = sourceOffset / AudioPacket.BytesPerFrame;
                _partialPtsMs = inputStartPtsMs +
                    (uint)((long)consumedFrames * 1000 / AudioPacket.SampleRate);
            }

            int copy = Math.Min(AudioPacket.PayloadSize - _partialLength, length - sourceOffset);
            Buffer.BlockCopy(pcm, sourceOffset, _partial, _partialLength, copy);
            _partialLength += copy;
            sourceOffset += copy;

            if (_partialLength == AudioPacket.PayloadSize)
            {
                SendPacket(_partialPtsMs);
                _partialLength = 0;
            }
        }
    }

    private void SendPacket(uint ptsMs)
    {
        var client = _clientEndpoint;
        if (client == null)
            return;

        AudioPacket.WriteHeader(_datagram, _sequence++, ptsMs);
        Buffer.BlockCopy(_partial, 0, _datagram, AudioPacket.HeaderSize, AudioPacket.PayloadSize);
        try
        {
            int sent = _socket.SendTo(_datagram, 0, _datagram.Length, SocketFlags.None, client);
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, sent);
        }
        catch (SocketException) { }
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
