using System.Buffers.Binary;

namespace DeskStreamer.Server.Protocol;

/// <summary>One complete Android gamepad state snapshot (PROTOCOL.md §3B).</summary>
public readonly record struct GamepadState(
    byte ControllerId,
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    uint Sequence);

public static class GamepadPacket
{
    public const int Size = 24;
    public const byte Version = 1;

    public static bool TryParse(ReadOnlySpan<byte> data, out GamepadState state)
    {
        state = default;
        if (data.Length != Size ||
            data[0] != (byte)'D' || data[1] != (byte)'S' ||
            data[2] != (byte)'G' || data[3] != (byte)'P' ||
            data[4] != Version || data[5] > 3)
        {
            return false;
        }

        state = new GamepadState(
            ControllerId: data[5],
            Buttons: BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2)),
            LeftTrigger: data[8],
            RightTrigger: data[9],
            LeftX: BinaryPrimitives.ReadInt16BigEndian(data.Slice(10, 2)),
            LeftY: BinaryPrimitives.ReadInt16BigEndian(data.Slice(12, 2)),
            RightX: BinaryPrimitives.ReadInt16BigEndian(data.Slice(14, 2)),
            RightY: BinaryPrimitives.ReadInt16BigEndian(data.Slice(16, 2)),
            Sequence: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(18, 4)));
        return true;
    }
}
