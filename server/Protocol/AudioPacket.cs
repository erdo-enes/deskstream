using System.Buffers.Binary;

namespace DeskStreamer.Server.Protocol;

/// <summary>
/// Fixed low-latency audio datagram defined by PROTOCOL.md §3A. Header fields are
/// big-endian; the PCM payload is signed 16-bit little-endian stereo.
/// </summary>
public static class AudioPacket
{
    public const int HeaderSize = 16;
    public const int Version = 1;
    public const int FormatPcmS16Le = 1;
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BytesPerSample = 2;
    public const int BytesPerFrame = Channels * BytesPerSample;
    public const int SamplesPerPacket = 240; // 5 ms at 48 kHz
    public const int PayloadSize = SamplesPerPacket * BytesPerFrame;
    public const int DatagramSize = HeaderSize + PayloadSize;

    public static void WriteHeader(Span<byte> dst, uint sequence, uint ptsMs)
    {
        dst[0] = Version;
        dst[1] = FormatPcmS16Le;
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2, 2), PayloadSize);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(8, 4), ptsMs);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(12, 2), SamplesPerPacket);
        dst[14] = 0;
        dst[15] = 0;
    }
}
