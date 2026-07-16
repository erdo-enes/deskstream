using System.Buffers.Binary;

namespace DeskStreamer.Server.Protocol;

/// <summary>
/// Optional per-frame timing sidecar sent after a frame's video and FEC datagrams. Keeping this
/// out of the v1 media header preserves compatibility with clients that do not understand tracing.
/// All timestamps use the server monotonic clock in microseconds. CaptureEndUs and ConvertEndUs
/// mark D3D11 command submission rather than GPU completion; measuring GPU execution itself would
/// require an asynchronously-resolved timestamp-query ring so tracing never stalls the hot path.
/// </summary>
public readonly record struct ServerFrameTrace(
    uint FrameId,
    long CaptureStartUs,
    long CaptureEndUs,
    long ConvertEndUs,
    long EncodeSubmitUs,
    long EncodeFinishUs,
    long PacketStartUs,
    long PacketEndUs);

public static class FrameTracePacket
{
    public const int Size = 68;
    public const byte Version = 1;
    private static ReadOnlySpan<byte> Magic => "DSTR"u8;

    public static void Write(Span<byte> destination, in ServerFrameTrace trace)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Frame trace destination must be at least {Size} bytes");

        Magic.CopyTo(destination);
        destination[4] = Version;
        destination[5] = destination[6] = destination[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), trace.FrameId);
        WriteInt64(destination.Slice(12, 8), trace.CaptureStartUs);
        WriteInt64(destination.Slice(20, 8), trace.CaptureEndUs);
        WriteInt64(destination.Slice(28, 8), trace.ConvertEndUs);
        WriteInt64(destination.Slice(36, 8), trace.EncodeSubmitUs);
        WriteInt64(destination.Slice(44, 8), trace.EncodeFinishUs);
        WriteInt64(destination.Slice(52, 8), trace.PacketStartUs);
        WriteInt64(destination.Slice(60, 8), trace.PacketEndUs);
    }

    private static void WriteInt64(Span<byte> destination, long value) =>
        BinaryPrimitives.WriteInt64BigEndian(destination, value);
}
