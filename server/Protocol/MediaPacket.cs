using System.Buffers.Binary;

namespace DeskStreamer.Server.Protocol;

/// <summary>
/// Media datagram header helpers. Wire layout is defined in PROTOCOL.md §3 and is
/// strictly big-endian for every multi-byte field.
///
/// offset size field
/// 0      1    version = 1
/// 1      1    flags        bit0 KEYFRAME, bit1 FEC
/// 2      2    payloadLen   (uint16 BE)
/// 4      4    frameId      (uint32 BE)
/// 8      2    packetIndex  (uint16 BE)
/// 10     2    packetCount  (uint16 BE)
/// 12     2    fecCount     (uint16 BE)
/// 14     4    ptsMs        (uint32 BE)
/// 18     2    reserved = 0
/// 20     ...  payload
/// </summary>
public static class MediaPacket
{
    public const int HeaderSize = 20;
    public const int MaxPayload = 1200;
    public const int MaxDatagram = HeaderSize + MaxPayload;

    public const byte Version = 1;
    public const byte FlagKeyframe = 0x01;
    public const byte FlagFec = 0x02;

    /// <summary>Writes the 20-byte header into <paramref name="dst"/> (must be >= 20 bytes).</summary>
    public static void WriteHeader(
        Span<byte> dst,
        byte flags,
        ushort payloadLen,
        uint frameId,
        ushort packetIndex,
        ushort packetCount,
        ushort fecCount,
        uint ptsMs)
    {
        dst[0] = Version;
        dst[1] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2, 2), payloadLen);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4), frameId);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(8, 2), packetIndex);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(10, 2), packetCount);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(12, 2), fecCount);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(14, 4), ptsMs);
        dst[18] = 0;
        dst[19] = 0;
    }
}
