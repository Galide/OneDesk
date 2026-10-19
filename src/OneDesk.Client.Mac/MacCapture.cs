using System.Runtime.InteropServices;
using OneDesk.Core;
using static OneDesk.Client.MacNative;

namespace OneDesk.Client;

public enum MacMouseKind
{
    Move,
    ButtonDown,
    ButtonUp,
    Wheel
}

/// <summary>One captured key transition. <see cref="Modifier"/> marks FlagsChanged events.</summary>
public readonly record struct MacKeyEvent(ushort Key, bool Down, bool Repeat, bool Modifier, ulong Flags);

/// <summary>One captured mouse transition; positions are normalized over the whole desktop.</summary>
public readonly record struct MacMouseEvent(MacMouseKind Kind, int Button, float Nx, float Ny, int Delta, int HDelta);

/// <summary>
/// Session-level CGEventTap. While the macOS machine owns the keyboard and mouse this is what
/// reads and suppresses the local input before it is forwarded to the paired machine.
/// </summary>
public sealed class MacCapture : IDisposable
{
    private const ulong EventMask =
        (1UL << (int)CGEventType.LeftMouseDown) |
        (1UL << (int)CGEventType.LeftMouseUp) |
        (1UL << (int)CGEventType.RightMouseDown) |
        (1UL << (int)CGEventType.RightMouseUp) |
        (1UL << (int)CGEventType.MouseMoved) |
        (1UL << (int)CGEventType.LeftMouseDragged) |
        (1UL << (int)CGEventType.RightMouseDragged) |
        (1UL << (int)CGEventType.KeyDown) |
        (1UL << (int)CGEventType.KeyUp) |
        (1UL << (int)CGEventType.FlagsChanged) |
        (1UL << (int)CGEventType.ScrollWheel) |
        (1UL << (int)CGEventType.OtherMouseDown) |
        (1UL << (int)CGEventType.OtherMouseUp) |
        (1UL << (int)CGEventType.OtherMouseDragged);

    private readonly object _gate = new();
    private CGEventTapCallback? _callback;
    private Thread? _thread;
    private IntPtr _tap;
    private IntPtr _runLoop;
    private volatile bool _running;
    private bool _disposed;

    private CGRect _desktop;
    private bool _hasDesktop;
    private long _desktopTicks;
    private double _scrollY;
    private double _scrollX;

    /// <summary>Returns true when the event must be suppressed.</summary>
    public Func<MacKeyEvent, bool>? KeyHandler { get; set; }

    /// <summary>Returns true when the event must be suppressed.</summary>
    public Func<MacMouseEvent, bool>? MouseHandler { get; set; }

    public bool IsRunning => _running;

    /// <summary>True when macOS trusts the process for accessibility (needed for a tap).</summary>
    public static bool Trusted
    {
        get
        {
            try
            {
                return AXIsProcessTrusted();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_running) return true;
            if (_disposed) return false;
            _running = true;
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "OneDeskEventTap"
            };
            _thread.Start();
        }

        for (var i = 0; i < 150 && Volatile.Read(ref _tap) == IntPtr.Zero && _running; i++)
        {
            Thread.Sleep(10);
        }

        if (Volatile.Read(ref _tap) == IntPtr.Zero)
        {
            if (_running)
            {
                Log.Error("无法创建键盘/鼠标监听（CGEventTapCreate 返回空）。" +
                          "请在 系统设置 → 隐私与安全性 → 辅助功能 中勾选 OneDesk，然后重新启动。");
                Stop();
            }

            return false;
        }

        return true;
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            _running = false;
            thread = _thread;
            _thread = null;
        }

        var loop = Volatile.Read(ref _runLoop);
        if (loop != IntPtr.Zero)
        {
            try
            {
                CFRunLoopStop(loop);
                CFRunLoopWakeUp(loop);
            }
            catch
            {
                // ignored
            }
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            try
            {
                thread.Join(2000);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void RunLoop()
    {
        _callback = new CGEventTapCallback(OnEvent);
        var callback = Marshal.GetFunctionPointerForDelegate(_callback);

        var tap = CGEventTapCreate(CGEventTapLocation.Session, CGEventTapPlacement.HeadInsert,
            CGEventTapOptions.Default, EventMask, callback, IntPtr.Zero);
        if (tap == IntPtr.Zero)
        {
            _running = false;
            Volatile.Write(ref _tap, IntPtr.Zero);
            return;
        }

        var source = CFMachPortCreateRunLoopSource(IntPtr.Zero, tap, IntPtr.Zero);
        var mode = CFStringCreateWithCString(IntPtr.Zero, "OneDeskTap", CFStringEncodingUtf8);
        var loop = CFRunLoopGetCurrent();
        CFRunLoopAddSource(loop, source, mode);

        Volatile.Write(ref _tap, tap);
        Volatile.Write(ref _runLoop, loop);
        CGEventTapEnable(tap, true);
        Log.Info("键盘/鼠标监听已启动");

        while (_running)
        {
            var result = CFRunLoopRunInMode(mode, 0.2, true);

            // 1 = stopped, 2 = finished: nothing left to service in this mode. Do not spin.
            if ((result is 1 or 2) && _running) Thread.Sleep(50);
        }

        try
        {
            CGEventTapEnable(tap, false);
            CFRunLoopRemoveSource(loop, source, mode);
            CFRelease(source);
            CFRelease(mode);
            CFRelease(tap);
        }
        catch
        {
            // ignored
        }

        Volatile.Write(ref _tap, IntPtr.Zero);
        Volatile.Write(ref _runLoop, IntPtr.Zero);
        _callback = null;
        Log.Info("键盘/鼠标监听已停止");
    }

    private IntPtr OnEvent(IntPtr proxy, uint type, IntPtr @event, IntPtr userInfo)
    {
        if (type == (uint)CGEventType.TapDisabledByTimeout ||
            type == (uint)CGEventType.TapDisabledByUserInput)
        {
            var tap = Volatile.Read(ref _tap);
            if (tap != IntPtr.Zero)
            {
                CGEventTapEnable(tap, true);
            }

            return @event;
        }

        if (!_running || @event == IntPtr.Zero) return @event;

        try
        {
            var suppress = (CGEventType)type switch
            {
                CGEventType.KeyDown => OnKey(@event, down: true),
                CGEventType.KeyUp => OnKey(@event, down: false),
                CGEventType.FlagsChanged => OnFlags(@event),
                CGEventType.MouseMoved => OnMove(@event),
                CGEventType.LeftMouseDragged => OnMove(@event),
                CGEventType.RightMouseDragged => OnMove(@event),
                CGEventType.OtherMouseDragged => OnMove(@event),
                CGEventType.LeftMouseDown => OnButton(@event, 1, down: true),
                CGEventType.LeftMouseUp => OnButton(@event, 1, down: false),
                CGEventType.RightMouseDown => OnButton(@event, 2, down: true),
                CGEventType.RightMouseUp => OnButton(@event, 2, down: false),
                CGEventType.OtherMouseDown => OnOtherButton(@event, down: true),
                CGEventType.OtherMouseUp => OnOtherButton(@event, down: false),
                CGEventType.ScrollWheel => OnScroll(@event),
                _ => false
            };

            return suppress ? IntPtr.Zero : @event;
        }
        catch (Exception ex)
        {
            Log.Warn("处理输入事件失败: " + ex.Message);
            return @event;
        }
    }

    private bool OnKey(IntPtr @event, bool down)
    {
        var handler = KeyHandler;
        if (handler is null) return false;

        var key = (ushort)CGEventGetIntegerValueField(@event, CGEventField.KeyboardEventKeycode);
        var repeat = CGEventGetIntegerValueField(@event, CGEventField.KeyboardEventAutorepeat) != 0;
        var flags = CGEventGetFlags(@event);
        return handler(new MacKeyEvent(key, down, repeat, false, flags));
    }

    private bool OnFlags(IntPtr @event)
    {
        var handler = KeyHandler;
        if (handler is null) return false;

        var key = (ushort)CGEventGetIntegerValueField(@event, CGEventField.KeyboardEventKeycode);
        var flags = CGEventGetFlags(@event);
        var down = key switch
        {
            56 or 60 => (flags & FlagShift) != 0,
            59 or 62 => (flags & FlagControl) != 0,
            58 or 61 => (flags & FlagAlternate) != 0,
            55 or 54 => (flags & FlagCommand) != 0,
            57 => (flags & FlagAlphaShift) != 0,
            _ => false
        };

        return handler(new MacKeyEvent(key, down, false, true, flags));
    }

    private bool OnMove(IntPtr @event)
    {
        var handler = MouseHandler;
        if (handler is null) return false;
        var (nx, ny) = Normalize(@event);
        return handler(new MacMouseEvent(MacMouseKind.Move, 0, nx, ny, 0, 0));
    }

    private bool OnButton(IntPtr @event, int button, bool down)
    {
        var handler = MouseHandler;
        if (handler is null) return false;
        var (nx, ny) = Normalize(@event);
        return handler(new MacMouseEvent(down ? MacMouseKind.ButtonDown : MacMouseKind.ButtonUp,
            button, nx, ny, 0, 0));
    }

    private bool OnOtherButton(IntPtr @event, bool down)
    {
        var handler = MouseHandler;
        if (handler is null) return false;

        var number = (int)CGEventGetIntegerValueField(@event, CGEventField.MouseEventButtonNumber);
        // macOS numbers the middle button 2, back 3 and forward 4; the protocol uses
        // 3 = middle, 4 = back, 5 = forward.
        var button = number switch
        {
            2 => 3,
            3 => 4,
            4 => 5,
            _ => 3
        };

        var (nx, ny) = Normalize(@event);
        return handler(new MacMouseEvent(down ? MacMouseKind.ButtonDown : MacMouseKind.ButtonUp,
            button, nx, ny, 0, 0));
    }

    private bool OnScroll(IntPtr @event)
    {
        var handler = MouseHandler;
        if (handler is null) return false;

        var continuous = CGEventGetIntegerValueField(@event,
            CGEventField.ScrollWheelEventIsContinuous) != 0;

        int dy;
        int dx;
        if (continuous)
        {
            // Trackpads report pixel deltas; accumulate them into wheel notches.
            var pointY = CGEventGetIntegerValueField(@event, CGEventField.ScrollWheelEventPointDeltaAxis1);
            var pointX = CGEventGetIntegerValueField(@event, CGEventField.ScrollWheelEventPointDeltaAxis2);
            _scrollY += pointY / 20.0;
            _scrollX += pointX / 20.0;
            dy = (int)Math.Truncate(_scrollY);
            dx = (int)Math.Truncate(_scrollX);
            if (dy == 0 && dx == 0) return true;
            _scrollY -= dy;
            _scrollX -= dx;
        }
        else
        {
            dy = (int)CGEventGetIntegerValueField(@event, CGEventField.ScrollWheelEventDeltaAxis1);
            dx = (int)CGEventGetIntegerValueField(@event, CGEventField.ScrollWheelEventDeltaAxis2);
        }

        var (nx, ny) = Normalize(@event);
        return handler(new MacMouseEvent(MacMouseKind.Wheel, 0, nx, ny, dy * 120, dx * 120));
    }

    private (float Nx, float Ny) Normalize(IntPtr @event)
    {
        var point = CGEventGetLocation(@event);
        var bounds = DesktopBounds();
        var width = Math.Max(1, bounds.Size.Width);
        var height = Math.Max(1, bounds.Size.Height);
        var nx = (point.X - bounds.Origin.X) / width;
        var ny = (point.Y - bounds.Origin.Y) / height;
        return ((float)Math.Clamp(nx, 0, 1), (float)Math.Clamp(ny, 0, 1));
    }

    /// <summary>Union of every active display, refreshed occasionally (displays can be unplugged).</summary>
    private CGRect DesktopBounds()
    {
        var now = Environment.TickCount64;
        if (_hasDesktop && now - _desktopTicks < 2000) return _desktop;

        try
        {
            var ids = new uint[16];
            if (CGGetActiveDisplayList((uint)ids.Length, ids, out var count) == 0 && count > 0)
            {
                var bounds = CGDisplayBounds(ids[0]);
                double left = bounds.Origin.X, top = bounds.Origin.Y;
                double right = bounds.Origin.X + bounds.Size.Width;
                double bottom = bounds.Origin.Y + bounds.Size.Height;
                for (var i = 1; i < count; i++)
                {
                    var other = CGDisplayBounds(ids[i]);
                    left = Math.Min(left, other.Origin.X);
                    top = Math.Min(top, other.Origin.Y);
                    right = Math.Max(right, other.Origin.X + other.Size.Width);
                    bottom = Math.Max(bottom, other.Origin.Y + other.Size.Height);
                }

                _desktop = new CGRect
                {
                    Origin = new CGPoint(left, top),
                    Size = new CGSize { Width = right - left, Height = bottom - top }
                };
                _hasDesktop = true;
            }
        }
        catch
        {
            // ignored
        }

        if (!_hasDesktop)
        {
            _desktop = CGDisplayBounds(CGMainDisplayID());
            _hasDesktop = true;
        }

        _desktopTicks = now;
        return _desktop;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
