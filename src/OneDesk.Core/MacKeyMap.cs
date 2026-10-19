using System.Collections.Generic;

namespace OneDesk.Core;

/// <summary>
/// Windows virtual-key to Set 1 scan-code conversion, including the extended-key flag.
/// The wire format carries Windows scan codes (that is what the Windows client replays),
/// so a macOS host has to translate its own key codes into them.
/// </summary>
public static class ScanMap
{
    private static readonly Dictionary<int, (int Scan, bool Extended)> Table = new()
    {
        // Editing / navigation.
        [0x08] = (0x0E, false), // Backspace
        [0x09] = (0x0F, false), // Tab
        [0x0C] = (0x4C, false), // Clear (numpad 5)
        [0x0D] = (0x1C, false), // Enter
        [0x14] = (0x3A, false), // CapsLock
        [0x1B] = (0x01, false), // Escape
        [0x20] = (0x39, false), // Space
        [0x21] = (0x49, true), // PageUp
        [0x22] = (0x51, true), // PageDown
        [0x23] = (0x4F, true), // End
        [0x24] = (0x47, true), // Home
        [0x25] = (0x4B, true), // Left
        [0x26] = (0x48, true), // Up
        [0x27] = (0x4D, true), // Right
        [0x28] = (0x50, true), // Down
        [0x2D] = (0x52, true), // Insert
        [0x2E] = (0x53, true), // Delete

        // Digits.
        [0x30] = (0x0B, false), [0x31] = (0x02, false), [0x32] = (0x03, false),
        [0x33] = (0x04, false), [0x34] = (0x05, false), [0x35] = (0x06, false),
        [0x36] = (0x07, false), [0x37] = (0x08, false), [0x38] = (0x09, false),
        [0x39] = (0x0A, false),

        // Letters.
        [0x41] = (0x1E, false), [0x42] = (0x30, false), [0x43] = (0x2E, false),
        [0x44] = (0x20, false), [0x45] = (0x12, false), [0x46] = (0x21, false),
        [0x47] = (0x22, false), [0x48] = (0x23, false), [0x49] = (0x17, false),
        [0x4A] = (0x24, false), [0x4B] = (0x25, false), [0x4C] = (0x26, false),
        [0x4D] = (0x32, false), [0x4E] = (0x31, false), [0x4F] = (0x18, false),
        [0x50] = (0x19, false), [0x51] = (0x10, false), [0x52] = (0x13, false),
        [0x53] = (0x1F, false), [0x54] = (0x14, false), [0x55] = (0x16, false),
        [0x56] = (0x2F, false), [0x57] = (0x11, false), [0x58] = (0x2D, false),
        [0x59] = (0x15, false), [0x5A] = (0x2C, false),

        // Modifiers (the generic codes behave like the left-hand key).
        [0x10] = (0x2A, false), [0xA0] = (0x2A, false), [0xA1] = (0x36, false),
        [0x11] = (0x1D, false), [0xA2] = (0x1D, false), [0xA3] = (0x1D, true),
        [0x12] = (0x38, false), [0xA4] = (0x38, false), [0xA5] = (0x38, true),
        [0x5B] = (0x5B, true), [0x5C] = (0x5C, true), [0x5D] = (0x5D, true),

        // Function keys.
        [0x70] = (0x3B, false), [0x71] = (0x3C, false), [0x72] = (0x3D, false),
        [0x73] = (0x3E, false), [0x74] = (0x3F, false), [0x75] = (0x40, false),
        [0x76] = (0x41, false), [0x77] = (0x42, false), [0x78] = (0x43, false),
        [0x79] = (0x44, false), [0x7A] = (0x57, false), [0x7B] = (0x58, false),
        [0x7C] = (0x64, false), [0x7D] = (0x65, false), [0x7E] = (0x66, false),
        [0x7F] = (0x67, false), [0x80] = (0x68, false), [0x81] = (0x69, false),
        [0x82] = (0x6A, false), [0x83] = (0x6B, false), [0x84] = (0x6C, false),
        [0x85] = (0x6D, false), [0x86] = (0x6E, false), [0x87] = (0x76, false),

        // Keypad.
        [0x60] = (0x52, false), [0x61] = (0x4F, false), [0x62] = (0x50, false),
        [0x63] = (0x51, false), [0x64] = (0x4B, false), [0x65] = (0x4C, false),
        [0x66] = (0x4D, false), [0x67] = (0x47, false), [0x68] = (0x48, false),
        [0x69] = (0x49, false), [0x6A] = (0x37, false), [0x6B] = (0x4E, false),
        [0x6D] = (0x4A, false), [0x6E] = (0x53, false),
        [0x6F] = (0x35, true),

        // Locks.
        [0x90] = (0x45, false), [0x91] = (0x46, false),

        // OEM punctuation.
        [0xBA] = (0x27, false), [0xBB] = (0x0D, false), [0xBC] = (0x33, false),
        [0xBD] = (0x0C, false), [0xBE] = (0x34, false), [0xBF] = (0x35, false),
        [0xC0] = (0x29, false), [0xDB] = (0x1A, false), [0xDC] = (0x2B, false),
        [0xDD] = (0x1B, false), [0xDE] = (0x28, false), [0xE2] = (0x56, false)
    };

    public static bool TryGet(int vk, out int scan, out bool extended)
    {
        if (Table.TryGetValue(vk, out var value))
        {
            scan = value.Scan;
            extended = value.Extended;
            return true;
        }

        scan = 0;
        extended = false;
        return false;
    }

    /// <summary>All known pairs; the self-test compares them with the Windows API.</summary>
    public static IReadOnlyDictionary<int, (int Scan, bool Extended)> All => Table;
}

/// <summary>
/// macOS virtual key code to Windows VK / scan-code conversion. The reverse of
/// <see cref="KeyMap"/> plus the side-aware modifier keys that have no Windows VK of
/// their own. Used by the macOS host to feed the Windows client.
/// </summary>
public static class MacKeyMap
{
    private static readonly Dictionary<ushort, int> MacToVk = BuildMacToVk();

    private static readonly Dictionary<ushort, (int Vk, int Scan, bool Extended)> Special = new()
    {
        [52] = (0x0D, 0x1C, true), // keypad enter (PowerBook)
        [54] = (0x5C, 0x5C, true), // right command
        [55] = (0x5B, 0x5B, true), // left command
        [56] = (0xA0, 0x2A, false), // left shift
        [57] = (0x14, 0x3A, false), // caps lock
        [58] = (0xA4, 0x38, false), // left option
        [59] = (0xA2, 0x1D, false), // left control
        [60] = (0xA1, 0x36, false), // right shift
        [61] = (0xA5, 0x38, true), // right option
        [62] = (0xA3, 0x1D, true), // right control
        [76] = (0x0D, 0x1C, true) // keypad enter
    };

    private static Dictionary<ushort, int> BuildMacToVk()
    {
        var map = new Dictionary<ushort, int>();
        foreach (var pair in KeyMap.SimplePairs)
        {
            // A couple of VKs share a macOS key (Clear / NumLock); the first one wins.
            if (!map.ContainsKey(pair.Value)) map[pair.Value] = pair.Key;
        }

        return map;
    }

    /// <summary>Every derived macOS key code → Windows VK pair; the self-test walks it.</summary>
    public static IEnumerable<KeyValuePair<ushort, int>> Pairs => MacToVk;

    /// <summary>macOS key code to Windows virtual key, without the scan code.</summary>
    public static bool TryGetVk(ushort macKey, out int vk)
    {
        if (Special.TryGetValue(macKey, out var special))
        {
            vk = special.Vk;
            return true;
        }

        return MacToVk.TryGetValue(macKey, out vk);
    }

    public static bool TryMap(ushort macKey, out int vk, out int scan, out bool extended)
    {
        if (Special.TryGetValue(macKey, out var special))
        {
            vk = special.Vk;
            scan = special.Scan;
            extended = special.Extended;
            return true;
        }

        if (!MacToVk.TryGetValue(macKey, out vk))
        {
            scan = 0;
            extended = false;
            return false;
        }

        return ScanMap.TryGet(vk, out scan, out extended);
    }

    /// <summary>True for the keys that arrive as FlagsChanged rather than KeyDown/KeyUp.</summary>
    public static bool IsModifier(ushort macKey) =>
        macKey is 54 or 55 or 56 or 57 or 58 or 59 or 60 or 61 or 62;

    /// <summary>
    /// Matches a macOS key plus its modifier state against the (Windows-shaped) hotkey
    /// configuration. Command plays the role of the Windows key.
    /// </summary>
    public static bool MatchesHotkey(HotkeySpec hotkey, ushort macKey,
        bool ctrl, bool alt, bool shift, bool command)
    {
        return TryGetVk(macKey, out var vk) && hotkey.Matches(ctrl, alt, shift, command, vk);
    }
}
