namespace OneDesk.Core;

[Flags]
public enum MacModifiers : ulong
{
    None = 0,
    AlphaShift = 1UL << 16,
    Shift = 1UL << 17,
    Control = 1UL << 18,
    Option = 1UL << 19,
    Command = 1UL << 20
}

/// <summary>Windows virtual-key / scan-code to macOS virtual-key conversion.</summary>
public static class KeyMap
{
    // US ANSI macOS virtual key codes.
    private static readonly Dictionary<int, ushort> Simple = new()
    {
        [0x08] = 51, // Backspace -> Delete
        [0x09] = 48, // Tab
        [0x0C] = 71, // Clear
        [0x0D] = 36, // Enter
        [0x14] = 57, // CapsLock
        [0x1B] = 53, // Escape
        [0x20] = 49, // Space
        [0x21] = 116, // PageUp
        [0x22] = 121, // PageDown
        [0x23] = 119, // End
        [0x24] = 115, // Home
        [0x25] = 123, // Left
        [0x26] = 126, // Up
        [0x27] = 124, // Right
        [0x28] = 125, // Down
        [0x2D] = 114, // Insert -> Help
        [0x2E] = 117, // Delete -> Forward Delete
        [0x30] = 29, [0x31] = 18, [0x32] = 19, [0x33] = 20, [0x34] = 21,
        [0x35] = 23, [0x36] = 22, [0x37] = 26, [0x38] = 28, [0x39] = 25,
        [0x41] = 0, [0x42] = 11, [0x43] = 8, [0x44] = 2, [0x45] = 14,
        [0x46] = 3, [0x47] = 5, [0x48] = 4, [0x49] = 34, [0x4A] = 38,
        [0x4B] = 40, [0x4C] = 37, [0x4D] = 46, [0x4E] = 45, [0x4F] = 31,
        [0x50] = 35, [0x51] = 12, [0x52] = 15, [0x53] = 1, [0x54] = 17,
        [0x55] = 32, [0x56] = 9, [0x57] = 13, [0x58] = 7, [0x59] = 16,
        [0x5A] = 6,
        [0x60] = 82, [0x61] = 83, [0x62] = 84, [0x63] = 85, [0x64] = 86,
        [0x65] = 87, [0x66] = 88, [0x67] = 89, [0x68] = 91, [0x69] = 92,
        [0x6A] = 67, // Multiply
        [0x6B] = 69, // Add
        [0x6D] = 78, // Subtract
        [0x6E] = 65, // Decimal
        [0x6F] = 75, // Divide
        [0x70] = 122, [0x71] = 120, [0x72] = 99, [0x73] = 118,
        [0x74] = 96, [0x75] = 97, [0x76] = 98, [0x77] = 100,
        [0x78] = 101, [0x79] = 109, [0x7A] = 103, [0x7B] = 111,
        [0x7C] = 105, [0x7D] = 107, [0x7E] = 113, [0x7F] = 106,
        [0x80] = 64, [0x81] = 79, [0x82] = 80, [0x83] = 90,
        [0x90] = 71, // NumLock -> Clear
        [0xBA] = 41, [0xBB] = 24, [0xBC] = 43, [0xBD] = 27,
        [0xBE] = 47, [0xBF] = 44, [0xC0] = 50,
        [0xDB] = 33, [0xDC] = 42, [0xDD] = 30, [0xDE] = 39,
        [0xE2] = 10  // OEM_102 -> ISO section
    };

    /// <summary>Every simple VK → macOS key-code pair; used to build the reverse table.</summary>
    public static IEnumerable<KeyValuePair<int, ushort>> SimplePairs => Simple;

    public static bool IsModifier(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static MacModifiers ModifierFlag(int vk, bool swapCtrlCmd)
    {
        return vk switch
        {
            0x10 or 0xA0 or 0xA1 => MacModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => swapCtrlCmd ? MacModifiers.Command : MacModifiers.Control,
            0x12 or 0xA4 or 0xA5 => MacModifiers.Option,
            0x5B or 0x5C => swapCtrlCmd ? MacModifiers.Control : MacModifiers.Command,
            _ => MacModifiers.None
        };
    }

    public static ushort? ToMacKeyCode(int vk, int scan, bool extended, bool swapCtrlCmd)
    {
        // Modifiers need side-aware handling.
        switch (vk)
        {
            case 0x10:
            case 0xA0:
            case 0xA1:
                return (ushort)(scan == 0x36 ? 60 : 56);
            case 0x11:
            case 0xA2:
            case 0xA3:
                if (swapCtrlCmd)
                {
                    // Ctrl becomes Command.
                    return (ushort)(extended ? 54 : 55);
                }

                return (ushort)(extended ? 62 : 59);
            case 0x12:
            case 0xA4:
            case 0xA5:
                return (ushort)(extended ? 61 : 58);
            case 0x5B:
            case 0x5C:
                if (swapCtrlCmd) return (ushort)(extended ? 62 : 59);
                return 55; // Command
            case 0x0D:
                return (ushort)(extended ? 76 : 36);
            case 0x2D:
                return (ushort)(extended ? 114 : 82);
            case 0x2E:
                return (ushort)(extended ? 117 : 65);
        }

        // Note: 0 is a valid macOS key code (kVK_ANSI_A), so unsupported
        // keys must be absent from the table rather than mapped to zero.
        if (Simple.TryGetValue(vk, out var code)) return code;
        return null;
    }
}
