using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OneDesk.Core;
using static OneDesk.Platform.Win.NativeWin;

namespace OneDesk.Platform.Win;

/// <summary>
/// Replays forwarded keyboard and mouse events into the local Windows session with SendInput.
/// Mouse movement uses absolute coordinates, which is what the protocol carries.
/// </summary>
public sealed class InputInjector : IDisposable
{
    /// <summary>Scan codes above this are not real and must be sent by virtual key instead.</summary>
    private const int MaxScanCode = 0xFF;

    private readonly ConcurrentDictionary<int, (int Scan, bool Extended)> _pressedKeys = new();
    private readonly ConcurrentDictionary<int, byte> _pressedButtons = new();
    private volatile bool _blocked;
    private bool _disposed;

    /// <summary>
    /// True when the last injection was rejected with ERROR_ACCESS_DENIED, which happens when the
    /// foreground window runs at a higher integrity level (UIPI). Restarting as administrator fixes it.
    /// </summary>
    public bool Blocked => _blocked;

    /// <summary>Number of keys and buttons currently held down on the remote side.</summary>
    public int HeldCount => _pressedKeys.Count + _pressedButtons.Count;

    public void Key(int vk, int scan, KeyFlags flags)
    {
        if (_disposed) return;

        var down = (flags & KeyFlags.Down) != 0;
        if (down) _pressedKeys[vk] = (scan, (flags & KeyFlags.Extended) != 0);
        else _pressedKeys.TryRemove(vk, out _);

        Send(new[] { KeyInput(vk, scan, flags) });
    }

    public void MouseMove(float nx, float ny)
    {
        if (_disposed) return;
        Send(new[] { MoveInput(nx, ny) });
    }

    public void MouseButton(int button, bool down, float nx, float ny)
    {
        if (_disposed) return;

        var flag = WinInputMap.ButtonFlag(button, down);
        if (flag == 0)
        {
            Log.Warn($"收到未知的鼠标按键编号 {button}，已忽略");
            return;
        }

        if (down) _pressedButtons.TryAdd(button, 0);
        else _pressedButtons.TryRemove(button, out _);

        var data = WinInputMap.IsXButton(button) ? WinInputMap.XButtonData(button) : 0u;
        Send(new[] { MoveInput(nx, ny), MouseInput(0, 0, data, flag) });
    }

    public void Wheel(int delta, int hDelta, float nx, float ny)
    {
        if (_disposed) return;

        var inputs = new List<INPUT>(3) { MoveInput(nx, ny) };
        if (delta != 0) inputs.Add(MouseInput(0, 0, unchecked((uint)delta), WinInputMap.Wheel));
        if (hDelta != 0) inputs.Add(MouseInput(0, 0, unchecked((uint)hDelta), WinInputMap.HorizontalWheel));
        if (inputs.Count > 1) Send(inputs.ToArray());
    }

    /// <summary>
    /// Releases everything this injector still holds down, so handing control back never leaves a
    /// stuck key or a stuck mouse button on the remote machine.
    /// </summary>
    public void ReleaseAll()
    {
        if (_disposed) return;

        var inputs = new List<INPUT>(_pressedKeys.Count + _pressedButtons.Count);

        foreach (var button in _pressedButtons.Keys)
        {
            var flag = WinInputMap.ButtonFlag(button, false);
            if (flag == 0) continue;
            var data = WinInputMap.IsXButton(button) ? WinInputMap.XButtonData(button) : 0u;
            inputs.Add(MouseInput(0, 0, data, flag));
        }

        foreach (var (vk, info) in _pressedKeys)
        {
            // Keep the extended prefix: without it an extended key-up would release the wrong key.
            var release = info.Extended ? KeyFlags.Extended : KeyFlags.None;
            inputs.Add(KeyInput(vk, info.Scan, release));
        }

        _pressedButtons.Clear();
        _pressedKeys.Clear();

        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    private static INPUT MoveInput(float nx, float ny) => MouseInput(
        WinInputMap.ToAbsolute(nx),
        WinInputMap.ToAbsolute(ny),
        0,
        WinInputMap.MouseMove | WinInputMap.Absolute);

    private static INPUT MouseInput(int dx, int dy, uint data, uint flags) => new()
    {
        type = INPUT_MOUSE,
        U = new INPUTUNION
        {
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static INPUT KeyInput(int vk, int scan, KeyFlags flags)
    {
        var hasScan = scan > 0 && scan <= MaxScanCode;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = hasScan ? (ushort)0 : (ushort)(vk & 0xFFFF),
                    wScan = hasScan ? (ushort)scan : (ushort)0,
                    dwFlags = WinInputMap.KeyEventFlags(flags, hasScan),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        return input;
    }

    private void Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;

        try
        {
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent == (uint)inputs.Length)
            {
                _blocked = false;
                return;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_ACCESS_DENIED)
            {
                if (!_blocked)
                {
                    _blocked = true;
                    Log.Warn("输入注入被系统拒绝（前台窗口以管理员身份运行）。请以管理员身份运行 OneDesk 客户端。");
                }

                return;
            }

            Log.Warn($"SendInput 仅发送 {sent}/{inputs.Length} 个事件，错误码 {error}");
        }
        catch (Exception ex)
        {
            Log.Error("SendInput 失败", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            ReleaseAll();
        }
        catch
        {
            // ignored
        }

        _disposed = true;
    }
}
