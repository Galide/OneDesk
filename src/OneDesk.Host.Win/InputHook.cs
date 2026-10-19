using System.Runtime.InteropServices;
using OneDesk.Core;
using static OneDesk.Host.Native;

namespace OneDesk.Host;

public enum HookMouseKind
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
    MiddleDown,
    MiddleUp,
    XDown,
    XUp,
    Wheel,
    HWheel
}

public readonly record struct HookKeyEvent(int Vk, int Scan, bool Down, bool Extended, bool Injected);
public readonly record struct HookMouseEvent(HookMouseKind Kind, int X, int Y, int Data, bool Injected);

/// <summary>
/// Global low-level keyboard and mouse hook. Runs its own message pump; handlers must return fast.
/// </summary>
public sealed class InputHook : IDisposable
{
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private volatile bool _started;
    private volatile bool _disposed;

    public Func<HookKeyEvent, bool>? KeyHandler { get; set; }
    public Func<HookMouseEvent, bool>? MouseHandler { get; set; }

    public bool IsRunning => _started;

    public InputHook()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public bool Start()
    {
        if (_thread is not null) return _started;
        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "OneDesk input hook",
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
        if (!_ready.Wait(3000))
        {
            Log.Error("安装键盘/鼠标钩子超时");
            return false;
        }

        return _started;
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandle(null);

        try
        {
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) 失败: " + Marshal.GetLastWin32Error());
            }

            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                Log.Error("SetWindowsHookEx(WH_MOUSE_LL) 失败: " + Marshal.GetLastWin32Error());
            }

            _started = _keyboardHook != IntPtr.Zero;
        }
        finally
        {
            _ready.Set();
        }

        if (!_started) return;

        while (!_disposed && GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _started = false;
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLKHF_INJECTED) != 0)
            {
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            var down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            var ev = new HookKeyEvent((int)data.vkCode, (int)data.scanCode, down,
                (data.flags & LLKHF_EXTENDED) != 0, false);
            if (KeyHandler?.Invoke(ev) == true)
            {
                return (IntPtr)1;
            }
        }
        catch (Exception ex)
        {
            Log.Error("键盘钩子处理异常", ex);
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLMHF_INJECTED) != 0)
            {
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            HookMouseEvent? ev = null;
            switch ((int)wParam)
            {
                case WM_MOUSEMOVE:
                    ev = new HookMouseEvent(HookMouseKind.Move, data.pt.X, data.pt.Y, 0, false);
                    break;
                case WM_LBUTTONDOWN:
                    ev = new HookMouseEvent(HookMouseKind.LeftDown, data.pt.X, data.pt.Y, 0, false);
                    break;
                case WM_LBUTTONUP:
                    ev = new HookMouseEvent(HookMouseKind.LeftUp, data.pt.X, data.pt.Y, 0, false);
                    break;
                case WM_RBUTTONDOWN:
                    ev = new HookMouseEvent(HookMouseKind.RightDown, data.pt.X, data.pt.Y, 0, false);
                    break;
                case WM_RBUTTONUP:
                    ev = new HookMouseEvent(HookMouseKind.RightUp, data.pt.X, data.pt.Y, 0, false);
                    break;
                case WM_MBUTTONDOWN:
                    ev = new HookMouseEvent(HookMouseKind.MiddleDown, data.pt.X, data.pt.Y, 0, false);
                    break;
                case WM_MBUTTONUP:
                    ev = new HookMouseEvent(HookMouseKind.MiddleUp, data.pt.X, data.pt.Y, 0, false);
                    break;
                case WM_MOUSEWHEEL:
                    ev = new HookMouseEvent(HookMouseKind.Wheel, data.pt.X, data.pt.Y,
                        (short)((data.mouseData >> 16) & 0xFFFF), false);
                    break;
                case WM_MOUSEHWHEEL:
                    ev = new HookMouseEvent(HookMouseKind.HWheel, data.pt.X, data.pt.Y,
                        (short)((data.mouseData >> 16) & 0xFFFF), false);
                    break;
                case WM_XBUTTONDOWN:
                    ev = new HookMouseEvent(HookMouseKind.XDown, data.pt.X, data.pt.Y,
                        (int)((data.mouseData >> 16) & 0xFFFF), false);
                    break;
                case WM_XBUTTONUP:
                    ev = new HookMouseEvent(HookMouseKind.XUp, data.pt.X, data.pt.Y,
                        (int)((data.mouseData >> 16) & 0xFFFF), false);
                    break;
            }

            if (ev is not null && MouseHandler?.Invoke(ev.Value) == true)
            {
                return (IntPtr)1;
            }
        }
        catch (Exception ex)
        {
            Log.Error("鼠标钩子处理异常", ex);
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        try
        {
            _thread?.Join(1500);
        }
        catch
        {
            // ignored
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _ready.Dispose();
    }
}
