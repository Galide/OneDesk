using OneDesk.Core;
using static OneDesk.Client.MacNative;

namespace OneDesk.Client;

/// <summary>Injects keyboard and mouse events with CoreGraphics.</summary>
public sealed class MacInput : IDisposable
{
    private readonly IntPtr _source;
    private readonly HashSet<ushort> _downKeys = new();
    private readonly HashSet<int> _downButtons = new();
    private readonly HashSet<int> _unsupportedKeys = new();
    private MacModifiers _flags;
    private MacDisplayInfo? _display;
    private CGPoint _position;
    private double _scrollX;
    private double _scrollY;
    private bool _invertScroll;
    private bool _disposed;

    public MacInput()
    {
        _source = CGEventSourceCreate(CGEventSourceStateID.HIDSystem);
        CGAssociateMouseAndMouseCursorPosition(1);
    }

    public MacDisplayInfo? Display => _display;

    public void Configure(bool invertScroll)
    {
        _invertScroll = invertScroll;
    }

    public void RefreshDisplay(string preferredUuid)
    {
        _display = MacDisplay.Pick(preferredUuid);
        if (_display is not null)
        {
            _position = Center(_display.Bounds);
        }
        else
        {
            Log.Warn("没有找到可用的 macOS 显示器");
        }
    }

    public void WarpToCenter()
    {
        if (_display is null) return;
        _position = Center(_display.Bounds);
        try
        {
            CGWarpMouseCursorPosition(_position);
            CGAssociateMouseAndMouseCursorPosition(1);
        }
        catch
        {
            // ignored
        }
    }

    public void Key(int vk, int scan, KeyFlags flags, bool swapCtrlCmd)
    {
        var down = flags.HasFlag(KeyFlags.Down);
        var extended = flags.HasFlag(KeyFlags.Extended);
        var code = KeyMap.ToMacKeyCode(vk, scan, extended, swapCtrlCmd);
        if (code is null)
        {
            if (_unsupportedKeys.Add(vk))
            {
                Log.Warn($"不支持的按键 VK=0x{vk:X2}（已在 Mac 端忽略）");
            }

            return;
        }

        var modifier = KeyMap.ModifierFlag(vk, swapCtrlCmd);
        if (modifier != MacModifiers.None)
        {
            if (down) _flags |= modifier;
            else _flags &= ~modifier;
        }

        var ev = CGEventCreateKeyboardEvent(_source, code.Value, down);
        if (ev == IntPtr.Zero) return;
        try
        {
            CGEventSetFlags(ev, (ulong)_flags);
            if (flags.HasFlag(KeyFlags.Repeat))
            {
                CGEventSetIntegerValueField(ev, CGEventField.KeyboardEventAutorepeat, 1);
            }

            CGEventPost(CGEventTapLocation.HID, ev);
        }
        finally
        {
            CFRelease(ev);
        }

        if (down) _downKeys.Add(code.Value);
        else _downKeys.Remove(code.Value);
    }

    public void MouseMove(float nx, float ny)
    {
        _position = Map(nx, ny);
        var type = CGEventType.MouseMoved;
        var button = 0;
        if (_downButtons.Contains(1))
        {
            type = CGEventType.LeftMouseDragged;
        }
        else if (_downButtons.Contains(2))
        {
            type = CGEventType.RightMouseDragged;
            button = 1;
        }
        else if (_downButtons.Count > 0)
        {
            type = CGEventType.OtherMouseDragged;
            button = _downButtons.Min() switch
            {
                3 => 2,
                4 => 3,
                5 => 4,
                _ => 2
            };
        }

        PostMouse(type, _position, button);
    }

    public void MouseButton(int button, bool down, float nx, float ny)
    {
        _position = Map(nx, ny);
        var macButton = button switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            4 => 3,
            5 => 4,
            _ => 0
        };

        var type = macButton switch
        {
            0 => down ? CGEventType.LeftMouseDown : CGEventType.LeftMouseUp,
            1 => down ? CGEventType.RightMouseDown : CGEventType.RightMouseUp,
            _ => down ? CGEventType.OtherMouseDown : CGEventType.OtherMouseUp
        };

        PostMouse(type, _position, macButton);
        if (down) _downButtons.Add(button);
        else _downButtons.Remove(button);
    }

    public void Wheel(int delta, int hDelta, float nx, float ny)
    {
        _position = Map(nx, ny);
        _scrollY += delta / 120.0;
        _scrollX += hDelta / 120.0;

        var linesY = (int)Math.Truncate(_scrollY);
        var linesX = (int)Math.Truncate(_scrollX);
        if (linesY == 0 && linesX == 0) return;

        _scrollY -= linesY;
        _scrollX -= linesX;
        if (_invertScroll) linesY = -linesY;

        var ev = CGEventCreateScrollWheelEvent2(_source, CGScrollEventUnit.Line, 2, linesY, linesX, 0);
        if (ev == IntPtr.Zero) return;
        try
        {
            CGEventSetLocation(ev, _position);
            CGEventSetFlags(ev, (ulong)_flags);
            CGEventPost(CGEventTapLocation.HID, ev);
        }
        finally
        {
            CFRelease(ev);
        }
    }

    public void ReleaseAll()
    {
        foreach (var code in _downKeys.ToArray())
        {
            var ev = CGEventCreateKeyboardEvent(_source, code, false);
            if (ev == IntPtr.Zero) continue;
            try
            {
                CGEventSetFlags(ev, 0);
                CGEventPost(CGEventTapLocation.HID, ev);
            }
            finally
            {
                CFRelease(ev);
            }
        }

        _downKeys.Clear();
        _flags = MacModifiers.None;

        foreach (var button in _downButtons.ToArray())
        {
            var macButton = button switch
            {
                1 => 0,
                2 => 1,
                3 => 2,
                4 => 3,
                5 => 4,
                _ => 0
            };
            var type = macButton switch
            {
                0 => CGEventType.LeftMouseUp,
                1 => CGEventType.RightMouseUp,
                _ => CGEventType.OtherMouseUp
            };
            PostMouse(type, _position, macButton);
        }

        _downButtons.Clear();
        _scrollX = 0;
        _scrollY = 0;
    }

    private void PostMouse(CGEventType type, CGPoint point, int button)
    {
        var ev = CGEventCreateMouseEvent(_source, type, point, (CGMouseButton)button);
        if (ev == IntPtr.Zero) return;
        try
        {
            CGEventSetFlags(ev, (ulong)_flags);
            CGEventPost(CGEventTapLocation.HID, ev);
        }
        finally
        {
            CFRelease(ev);
        }
    }

    private CGPoint Map(float nx, float ny)
    {
        if (_display is null) RefreshDisplay("");
        var bounds = _display?.Bounds ?? new CGRect { Origin = new CGPoint(0, 0), Size = new CGSize { Width = 1920, Height = 1080 } };
        var x = bounds.Origin.X + Math.Clamp(nx, 0f, 1f) * Math.Max(1, bounds.Size.Width - 1);
        var y = bounds.Origin.Y + Math.Clamp(ny, 0f, 1f) * Math.Max(1, bounds.Size.Height - 1);
        return new CGPoint(x, y);
    }

    private static CGPoint Center(CGRect bounds) => new(
        bounds.Origin.X + bounds.Size.Width / 2,
        bounds.Origin.Y + bounds.Size.Height / 2);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            ReleaseAll();
        }
        catch
        {
            // ignored
        }

        if (_source != IntPtr.Zero) CFRelease(_source);
    }
}
