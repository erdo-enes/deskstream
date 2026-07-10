using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeskStreamer.Server.Input;

/// <summary>
/// Injects ordered USB HID keyboard-page transitions into the interactive Windows desktop.
/// The manager owns the authoritative pressed-key set so every teardown path can release all
/// keys even if the client disappears while a key is held.
/// </summary>
public sealed class RemoteKeyboardManager : IDisposable
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const ushort VkPause = 0x13;
    private const ushort VkVolumeMute = 0xAD;
    private const ushort VkVolumeDown = 0xAE;
    private const ushort VkVolumeUp = 0xAF;

    private readonly object _gate = new();
    private readonly HashSet<int> _pressed = new();
    private uint _lastSequence;
    private bool _hasSequence;
    private bool _releasePending;
    private bool _disposed;

    /// <summary>
    /// Applies one ordered physical-key transition. Unknown HID usages and stale transitions
    /// are deliberately ignored; duplicate down/up states are idempotent.
    /// </summary>
    public void SetKey(uint sequence, int usage, bool down)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_hasSequence && unchecked((int)(sequence - _lastSequence)) <= 0)
                return;
            _lastSequence = sequence;
            _hasSequence = true;

            // A live reset may have been unable to release one or more keys during a
            // transient desktop/UIPI transition. Retry before accepting new input so a
            // stale modifier cannot affect the next deliberate key.
            if (_releasePending)
            {
                ReleasePressedKeys(retainFailures: true);
                if (_releasePending)
                    return;
            }

            // Every well-formed KEYBOARD_KEY consumes its sequence even when the HID usage
            // is unsupported. This keeps duplicate/stale rejection coherent across the
            // complete keyboard stream.
            if (!TryMapUsage(usage, out KeyMapping mapping))
                return;

            bool isPressed = _pressed.Contains(usage);
            if (down == isPressed)
                return;

            Span<NativeInput> input = stackalloc NativeInput[1];
            input[0] = CreateInput(mapping, down);
            Send(input);

            if (down)
                _pressed.Add(usage);
            else
                _pressed.Remove(usage);
        }
    }

    /// <summary>Best-effort release of every key currently held by the remote client.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            ReleasePressedKeys(retainFailures: true);
            _hasSequence = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            // Mark terminal while holding the same gate used by SetKey. A call that was
            // already in flight finishes before this reset; a stale call that arrives after
            // teardown sees _disposed and can never inject into an orphaned manager.
            _disposed = true;
            ReleasePressedKeys(retainFailures: false);
            _pressed.Clear();
            _releasePending = false;
            _hasSequence = false;
        }
    }

    private void ReleasePressedKeys(bool retainFailures)
    {
        if (_pressed.Count == 0)
        {
            _releasePending = false;
            return;
        }

        // Release ordinary keys before modifiers so a disconnect cannot leave a modifier
        // affecting a locally pressed key during cleanup. Keep the usage order alongside
        // the INPUT array so a partial SendInput result removes only confirmed releases.
        var usages = new int[_pressed.Count];
        var releases = new NativeInput[_pressed.Count];
        int count = 0;
        foreach (int usage in _pressed)
        {
            if (usage >= 0xE0 || !TryMapUsage(usage, out KeyMapping mapping))
                continue;
            usages[count] = usage;
            releases[count++] = CreateInput(mapping, down: false);
        }
        foreach (int usage in _pressed)
        {
            if (usage < 0xE0 || !TryMapUsage(usage, out KeyMapping mapping))
                continue;
            usages[count] = usage;
            releases[count++] = CreateInput(mapping, down: false);
        }

        uint sent = count == 0 ? 0 : SendRaw(releases.AsSpan(0, count));
        int confirmed = Math.Min(count, (int)sent);
        for (int i = 0; i < confirmed; i++)
            _pressed.Remove(usages[i]);

        if (!retainFailures)
            _pressed.Clear();
        _releasePending = retainFailures && _pressed.Count > 0;
    }

    private static NativeInput CreateInput(in KeyMapping mapping, bool down)
    {
        uint flags = mapping.VirtualKey == 0 ? KeyEventScanCode : 0;
        if (mapping.Extended)
            flags |= KeyEventExtendedKey;
        if (!down)
            flags |= KeyEventKeyUp;

        return new NativeInput
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new NativeKeyboardInput
                {
                    VirtualKey = mapping.VirtualKey,
                    ScanCode = mapping.ScanCode,
                    Flags = flags,
                },
            },
        };
    }

    /// <summary>
    /// Maps USB HID Usage Tables keyboard/keypad page (0x07) positions to Windows Scan Code
    /// Set 1. Pause and the keyboard-page volume controls use virtual-key values because Pause
    /// begins with E1 and the volume usages have no official HID-to-PS/2 Set 1 translation.
    /// </summary>
    private static bool TryMapUsage(int usage, out KeyMapping mapping)
    {
        mapping = usage switch
        {
            // Letters A-Z.
            0x04 => new(0x1E), 0x05 => new(0x30), 0x06 => new(0x2E),
            0x07 => new(0x20), 0x08 => new(0x12), 0x09 => new(0x21),
            0x0A => new(0x22), 0x0B => new(0x23), 0x0C => new(0x17),
            0x0D => new(0x24), 0x0E => new(0x25), 0x0F => new(0x26),
            0x10 => new(0x32), 0x11 => new(0x31), 0x12 => new(0x18),
            0x13 => new(0x19), 0x14 => new(0x10), 0x15 => new(0x13),
            0x16 => new(0x1F), 0x17 => new(0x14), 0x18 => new(0x16),
            0x19 => new(0x2F), 0x1A => new(0x11), 0x1B => new(0x2D),
            0x1C => new(0x15), 0x1D => new(0x2C),

            // Number row, editing, whitespace, and punctuation.
            0x1E => new(0x02), 0x1F => new(0x03), 0x20 => new(0x04),
            0x21 => new(0x05), 0x22 => new(0x06), 0x23 => new(0x07),
            0x24 => new(0x08), 0x25 => new(0x09), 0x26 => new(0x0A),
            0x27 => new(0x0B), 0x28 => new(0x1C), 0x29 => new(0x01),
            0x2A => new(0x0E), 0x2B => new(0x0F), 0x2C => new(0x39),
            0x2D => new(0x0C), 0x2E => new(0x0D), 0x2F => new(0x1A),
            0x30 => new(0x1B), 0x31 => new(0x2B), 0x32 => new(0x2B),
            0x33 => new(0x27), 0x34 => new(0x28), 0x35 => new(0x29),
            0x36 => new(0x33), 0x37 => new(0x34), 0x38 => new(0x35),
            0x39 => new(0x3A),

            // F1-F12, Print Screen, Scroll Lock, and Pause.
            0x3A => new(0x3B), 0x3B => new(0x3C), 0x3C => new(0x3D),
            0x3D => new(0x3E), 0x3E => new(0x3F), 0x3F => new(0x40),
            0x40 => new(0x41), 0x41 => new(0x42), 0x42 => new(0x43),
            0x43 => new(0x44), 0x44 => new(0x57), 0x45 => new(0x58),
            0x46 => new(0x37, Extended: true), 0x47 => new(0x46),
            0x48 => new(ScanCode: 0, Extended: false, VirtualKey: VkPause),

            // Navigation cluster and arrows.
            0x49 => new(0x52, Extended: true), 0x4A => new(0x47, Extended: true),
            0x4B => new(0x49, Extended: true), 0x4C => new(0x53, Extended: true),
            0x4D => new(0x4F, Extended: true), 0x4E => new(0x51, Extended: true),
            0x4F => new(0x4D, Extended: true), 0x50 => new(0x4B, Extended: true),
            0x51 => new(0x50, Extended: true), 0x52 => new(0x48, Extended: true),

            // Numeric keypad and ISO/application keys.
            0x53 => new(0x45, Extended: true), 0x54 => new(0x35, Extended: true),
            0x55 => new(0x37), 0x56 => new(0x4A), 0x57 => new(0x4E),
            0x58 => new(0x1C, Extended: true), 0x59 => new(0x4F),
            0x5A => new(0x50), 0x5B => new(0x51), 0x5C => new(0x4B),
            0x5D => new(0x4C), 0x5E => new(0x4D), 0x5F => new(0x47),
            0x60 => new(0x48), 0x61 => new(0x49), 0x62 => new(0x52),
            0x63 => new(0x53), 0x64 => new(0x56),
            0x65 => new(0x5D, Extended: true), 0x66 => new(0x5E, Extended: true),
            0x67 => new(0x59),

            // F13-F24.
            0x68 => new(0x64), 0x69 => new(0x65), 0x6A => new(0x66),
            0x6B => new(0x67), 0x6C => new(0x68), 0x6D => new(0x69),
            0x6E => new(0x6A), 0x6F => new(0x6B), 0x70 => new(0x6C),
            0x71 => new(0x6D), 0x72 => new(0x6E), 0x73 => new(0x76),

            // Keyboard-page mute/volume keys.
            0x7F => new(ScanCode: 0, Extended: false, VirtualKey: VkVolumeMute),
            0x80 => new(ScanCode: 0, Extended: false, VirtualKey: VkVolumeUp),
            0x81 => new(ScanCode: 0, Extended: false, VirtualKey: VkVolumeDown),

            // Left/right modifiers.
            0xE0 => new(0x1D), 0xE1 => new(0x2A), 0xE2 => new(0x38),
            0xE3 => new(0x5B, Extended: true), 0xE4 => new(0x1D, Extended: true),
            0xE5 => new(0x36), 0xE6 => new(0x38, Extended: true),
            0xE7 => new(0x5C, Extended: true),
            _ => default,
        };

        return mapping.ScanCode != 0 || mapping.VirtualKey != 0;
    }

    private readonly record struct KeyMapping(
        ushort ScanCode,
        bool Extended = false,
        ushort VirtualKey = 0);

    private static unsafe void Send(ReadOnlySpan<NativeInput> inputs)
    {
        uint sent = SendRaw(inputs);
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows rejected remote keyboard input (elevated apps require an equally elevated server)");
    }

    private static unsafe uint SendRaw(ReadOnlySpan<NativeInput> inputs)
    {
        fixed (NativeInput* ptr = inputs)
        {
            return SendInput((uint)inputs.Length, ptr, Marshal.SizeOf<NativeInput>());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Union;
    }

    // Include the native union's largest member even though this manager fills only
    // KEYBDINPUT. SendInput rejects a cbSize smaller than the platform INPUT struct.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
        [FieldOffset(0)] public NativeMouseInput MouseSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint SendInput(uint count, NativeInput* inputs, int size);
}
