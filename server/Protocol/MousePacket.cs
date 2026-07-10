using System.Buffers.Binary;

namespace DeskStreamer.Server.Protocol;

public readonly record struct MouseMotion(
    uint Sequence,
    bool Absolute,
    int X,
    int Y,
    int HorizontalWheel,
    int VerticalWheel);

public readonly record struct CursorPosition(ushort X, ushort Y);

public static class CursorPacket
{
    public const int Size = 16;

    public static void Write(Span<byte> packet, uint sequence, CursorPosition position)
    {
        "DSMC"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = packet[6] = packet[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(8, 4), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(12, 2), position.X);
        BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(14, 2), position.Y);
    }
}

/// <summary>Parser for the authenticated 28-byte DSMI mouse-motion datagram.</summary>
public static class MousePacket
{
    public const int Size = 28;
    private static ReadOnlySpan<byte> Magic => "DSMI"u8;

    public static bool TryParse(ReadOnlySpan<byte> packet, out MouseMotion motion)
    {
        motion = default;
        if (packet.Length != Size || !packet[..4].SequenceEqual(Magic) || packet[4] != 1)
            return false;

        byte mode = packet[5];
        if (mode > 1 || packet[6] != 0 || packet[7] != 0)
            return false;

        int x = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(12, 4));
        int y = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(16, 4));
        if (mode == 1 && (x is < 0 or > 65535 || y is < 0 or > 65535))
            return false;

        motion = new MouseMotion(
            BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4)),
            mode == 1,
            x,
            y,
            BinaryPrimitives.ReadInt32BigEndian(packet.Slice(20, 4)),
            BinaryPrimitives.ReadInt32BigEndian(packet.Slice(24, 4)));
        return true;
    }
}
