namespace OneDesk.Core;

/// <summary>
/// Mapping helpers for the Windows client, the counterpart of <see cref="KeyMap"/> for macOS.
/// Both turn the wire format into platform events; this one is deliberately free of P/Invoke so
/// the self-test can exercise it on any platform (including the macOS CI job).
/// </summary>
public static class WinInputMap
{
    // MOUSEEVENTF_* flags from winuser.h.
    public const uint MouseMove = 0x0001;
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
    public const uint RightDown = 0x0008;
    public const uint RightUp = 0x0010;
    public const uint MiddleDown = 0x0020;
    public const uint MiddleUp = 0x0040;
    public const uint XDown = 0x0080;
    public const uint XUp = 0x0100;
    public const uint Wheel = 0x0800;
    public const uint HorizontalWheel = 0x1000;
    public const uint Absolute = 0x8000;

    // KEYEVENTF_* flags from winuser.h.
    public const uint ExtendedKey = 0x0001;
    public const uint KeyUp = 0x0002;
    public const uint ScanCode = 0x0008;

    public const int XButton1 = 0x0001;
    public const int XButton2 = 0x0002;

    /// <summary>
    /// Absolute mouse coordinates span 0..65535 across the primary monitor when
    /// MOUSEEVENTF_VIRTUALDESK is not set.
    /// </summary>
    public const int AbsoluteRange = 65535;

    /// <summary>Converts a 0..1 normalized coordinate into the 0..65535 absolute range.</summary>
    public static int ToAbsolute(double normalized)
    {
        if (double.IsNaN(normalized)) return 0;
        var clamped = Math.Clamp(normalized, 0d, 1d);
        return (int)Math.Round(clamped * AbsoluteRange, MidpointRounding.AwayFromZero);
    }

    /// <summary>Button numbering as sent by the host: 1 left, 2 right, 3 middle, 4/5 the X buttons.</summary>
    public static uint ButtonFlag(int button, bool down) => button switch
    {
        1 => down ? LeftDown : LeftUp,
        2 => down ? RightDown : RightUp,
        3 => down ? MiddleDown : MiddleUp,
        4 or 5 => down ? XDown : XUp,
        _ => 0
    };

    public static bool IsXButton(int button) => button is 4 or 5;

    public static uint XButtonData(int button) => button switch
    {
        4 => XButton1,
        5 => XButton2,
        _ => 0
    };

    public static uint WheelFlag(bool horizontal) => horizontal ? HorizontalWheel : WinInputMap.Wheel;

    /// <summary>
    /// Builds the KEYEVENTF_* mask for one key event. The extended prefix only has a meaning
    /// alongside KEYEVENTF_SCANCODE, so it is dropped when the event is sent by virtual key.
    /// </summary>
    public static uint KeyEventFlags(KeyFlags flags, bool hasScanCode)
    {
        var result = 0u;
        if (hasScanCode)
        {
            result |= ScanCode;
            if ((flags & KeyFlags.Extended) != 0) result |= ExtendedKey;
        }

        if ((flags & KeyFlags.Down) == 0) result |= KeyUp;
        return result;
    }
}
