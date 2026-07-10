using System.ComponentModel;
using System.Runtime.InteropServices;
using DeskStreamer.Server.Protocol;

namespace DeskStreamer.Server.Input;

/// <summary>
/// Injects mouse input into the interactive Windows desktop. Motion is newest-wins UDP;
/// ordered button transitions arrive over the authenticated control socket.
/// </summary>
public sealed class RemoteMouseManager : IDisposable
{
    private const uint InputMouse = 0;
    private const uint Move = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint XDown = 0x0080;
    private const uint XUp = 0x0100;
    private const uint Wheel = 0x0800;
    private const uint HWheel = 0x1000;
    private const uint MoveNoCoalesce = 0x2000;
    private const uint Absolute = 0x8000;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    private readonly object _gate = new();
    private readonly HashSet<string> _pressed = new(StringComparer.OrdinalIgnoreCase);
    private uint _lastMotionSequence;
    private uint _lastButtonSequence;
    private bool _hasMotionSequence;
    private bool _hasButtonSequence;

    public CursorPosition? Apply(in MouseMotion motion)
    {
        lock (_gate)
        {
            if (_hasMotionSequence && unchecked((int)(motion.Sequence - _lastMotionSequence)) <= 0)
                return null;
            _lastMotionSequence = motion.Sequence;
            _hasMotionSequence = true;

            Span<NativeInput> inputs = stackalloc NativeInput[3];
            int count = 0;
            if (motion.X != 0 || motion.Y != 0 || motion.Absolute)
            {
                uint flags = Move | MoveNoCoalesce;
                if (motion.Absolute) flags |= Absolute;
                inputs[count++] = MouseInput(motion.X, motion.Y, 0, flags);
            }
            if (motion.VerticalWheel != 0)
                inputs[count++] = MouseInput(0, 0, unchecked((uint)motion.VerticalWheel), Wheel);
            if (motion.HorizontalWheel != 0)
                inputs[count++] = MouseInput(0, 0, unchecked((uint)motion.HorizontalWheel), HWheel);
            if (count > 0)
                Send(inputs[..count]);
            return GetNormalizedCursorPosition();
        }
    }

    private static CursorPosition? GetNormalizedCursorPosition()
    {
        if (!GetCursorPos(out NativePoint point)) return null;
        int width = GetSystemMetrics(0);
        int height = GetSystemMetrics(1);
        if (width <= 1 || height <= 1) return null;
        ushort x = (ushort)Math.Clamp((long)point.X * 65535 / (width - 1), 0, 65535);
        ushort y = (ushort)Math.Clamp((long)point.Y * 65535 / (height - 1), 0, 65535);
        return new CursorPosition(x, y);
    }

    public void SetButton(uint sequence, string button, bool down)
    {
        lock (_gate)
        {
            if (_hasButtonSequence && unchecked((int)(sequence - _lastButtonSequence)) <= 0)
                return;
            _lastButtonSequence = sequence;
            _hasButtonSequence = true;

            string normalized = button.ToLowerInvariant();
            if (!TryButtonFlags(normalized, down, out uint flags, out uint data))
                return;
            if (down ? !_pressed.Add(normalized) : !_pressed.Remove(normalized))
                return;
            Span<NativeInput> input = stackalloc NativeInput[1];
            input[0] = MouseInput(0, 0, data, flags);
            Send(input);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_pressed.Count > 0)
            {
                Span<NativeInput> releases = stackalloc NativeInput[5];
                int count = 0;
                foreach (string button in _pressed)
                {
                    if (TryButtonFlags(button, false, out uint flags, out uint data))
                        releases[count++] = MouseInput(0, 0, data, flags);
                }
                if (count > 0)
                {
                    try { Send(releases[..count]); } catch { }
                }
            }
            _pressed.Clear();
            _hasMotionSequence = false;
            _hasButtonSequence = false;
        }
    }

    private static bool TryButtonFlags(string button, bool down, out uint flags, out uint data)
    {
        data = 0;
        flags = button switch
        {
            "left" => down ? LeftDown : LeftUp,
            "right" => down ? RightDown : RightUp,
            "middle" => down ? MiddleDown : MiddleUp,
            "back" => down ? XDown : XUp,
            "forward" => down ? XDown : XUp,
            _ => 0,
        };
        if (button == "back") data = XButton1;
        if (button == "forward") data = XButton2;
        return flags != 0;
    }

    private static NativeInput MouseInput(int x, int y, uint data, uint flags) => new()
    {
        Type = InputMouse,
        Union = new InputUnion
        {
            Mouse = new NativeMouseInput
            {
                Dx = x,
                Dy = y,
                MouseData = data,
                Flags = flags,
            },
        },
    };

    private static unsafe void Send(ReadOnlySpan<NativeInput> inputs)
    {
        fixed (NativeInput* ptr = inputs)
        {
            uint sent = SendInput((uint)inputs.Length, ptr, Marshal.SizeOf<NativeInput>());
            if (sent != inputs.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows rejected remote mouse input (elevated apps require an equally elevated server)");
        }
    }

    public void Dispose() => Reset();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint SendInput(uint count, NativeInput* inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
