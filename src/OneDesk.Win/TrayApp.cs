using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using OneDesk.Core;

namespace OneDesk.App;

internal static class TrayIconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static (Icon Icon, IntPtr Handle) Create(bool controlled, bool connected)
    {
        var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var accent = controlled
                ? Color.FromArgb(67, 209, 124)
                : connected
                    ? Color.FromArgb(91, 140, 255)
                    : Color.FromArgb(122, 134, 156);

            using var fill = new SolidBrush(Color.FromArgb(255, 23, 27, 35));
            using var pen = new Pen(accent, 2.6f);
            using var path = RoundedRect(new RectangleF(3.5f, 5.5f, 25f, 16f), 4f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            g.FillRectangle(fill, 14f, 21.5f, 4f, 4f);

            using var accentBrush = new SolidBrush(accent);
            g.FillRectangle(accentBrush, 9f, 26.5f, 14f, 2.6f);
            g.FillEllipse(accentBrush, 22f, 9f, 4f, 4f);
        }

        var handle = bitmap.GetHicon();
        bitmap.Dispose();
        return (Icon.FromHandle(handle), handle);
    }

    public static void Destroy((Icon Icon, IntPtr Handle) data)
    {
        data.Icon.Dispose();
        if (data.Handle != IntPtr.Zero) DestroyIcon(data.Handle);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Tray icon for both roles. Everything goes through <see cref="AppHost"/>.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AppHost _app;
    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ConcurrentQueue<(string Text, bool Error)> _notifications = new();

    private readonly ToolStripMenuItem _statusItem = new("状态：启动中") { Enabled = false };
    private readonly ToolStripMenuItem _switchItem = new("切换控制");
    private readonly ToolStripMenuItem _clipboardItem = new("发送剪贴板到对端");
    private readonly ToolStripMenuItem _testItem = new("测试显示器切换");
    private readonly ToolStripMenuItem _startupItem = new("开机自动启动");
    private readonly ToolStripMenuItem _hostRoleItem = new("主机（有键盘鼠标）");
    private readonly ToolStripMenuItem _clientRoleItem = new("客户端（受控）");

    private SettingsForm? _settings;
    private (Icon Icon, IntPtr Handle)? _iconData;
    private bool? _lastControlled;
    private bool? _lastConnected;
    private bool? _iconControlled;
    private bool? _iconConnected;
    private bool _lastBlocked;
    private volatile bool _dirty = true;
    private bool _disposed;

    public TrayApp(AppHost app, bool firstRun)
    {
        _app = app;

        BuildMenu();

        _notify = new NotifyIcon
        {
            Icon = GetIcon(),
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notify.DoubleClick += (_, _) => ShowSettings();

        _timer = new System.Windows.Forms.Timer { Interval = 700 };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();

        _app.Notification += OnNotification;
        _app.StateChanged += () => _dirty = true;

        RefreshState();

        if (firstRun || (app.Role == AppRole.Client && app.Config.PeerHost.Length == 0))
        {
            ShowSettings();
        }
        else
        {
            _notify.ShowBalloonTip(2500, "OneDesk 已在后台运行",
                "双击托盘图标打开设置，右键可以切换角色和显示器。", ToolTipIcon.Info);
        }
    }

    private void BuildMenu()
    {
        _menu.Font = Theme.Body;
        _menu.ShowImageMargin = false;

        _switchItem.Font = new Font(Theme.Family, 9.25f, FontStyle.Bold);
        _switchItem.Click += (_, _) => _ = Task.Run(() => _app.ToggleAsync());

        _clipboardItem.Click += async (_, _) =>
        {
            var result = await _app.SendClipboardAsync();
            ShowBalloon(result, false);
        };

        _testItem.Click += async (_, _) =>
        {
            if (!UiDialog.Confirm(null, "测试显示器切换",
                    "显示器会切换到对端接口，3 秒后自动切回。\n\n如果画面没有回来，请在对端手动切回。",
                    "开始测试"))
            {
                return;
            }

            var result = await _app.TestMonitorAsync();
            ShowBalloon(result, false);
        };

        _startupItem.Click += (_, _) =>
        {
            var enabled = !Startup.IsEnabled;
            Startup.SetEnabled(enabled);
            var config = _app.Config.Clone();
            config.StartAutomatically = enabled;
            try
            {
                config.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("保存开机启动设置失败: " + ex.Message);
            }

            RefreshState();
            ShowBalloon(enabled ? "已开启开机自动启动。" : "已关闭开机自动启动。", false);
        };

        _hostRoleItem.Click += async (_, _) => await ChangeRoleAsync(AppRole.Host);
        _clientRoleItem.Click += async (_, _) => await ChangeRoleAsync(AppRole.Client);

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_switchItem);
        _menu.Items.Add(_clipboardItem);
        _menu.Items.Add(_testItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenLog()));

        var roleMenu = new ToolStripMenuItem("角色");
        roleMenu.DropDownItems.Add(_hostRoleItem);
        roleMenu.DropDownItems.Add(_clientRoleItem);
        _menu.Items.Add(roleMenu);

        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
    }

    private async Task ChangeRoleAsync(AppRole role)
    {
        if (_app.Role == role) return;

        var config = _app.Config.Clone();
        config.Role = role;
        if (!await _app.ApplyConfigAsync(config))
        {
            ShowBalloon("切换角色失败，详情见日志。", true);
            return;
        }

        _settings?.ReloadFromApp();
        ShowBalloon(role == AppRole.Host
            ? "已切换为主机。现在等待客户端连接。"
            : "已切换为客户端。正在连接主机…", false);
        RefreshState();
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
            return;
        }

        _settings = new SettingsForm(_app);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void OpenLog()
    {
        var path = Log.FilePath;
        if (path is null || !File.Exists(path))
        {
            ShowBalloon("还没有日志文件。", true);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowBalloon("打开日志失败：" + ex.Message, true);
        }
    }

    private void OnNotification(string message, bool isError)
    {
        _notifications.Enqueue((message, isError));
        _dirty = true;
    }

    private void Pump()
    {
        while (_notifications.TryDequeue(out var item)) ShowBalloon(item.Text, item.Error);
        if (_dirty || _lastControlled != _app.HasControl || _lastConnected != _app.IsConnected ||
            _lastBlocked != _app.InjectionBlocked)
        {
            RefreshState();
        }
    }

    private void RefreshState()
    {
        _dirty = false;
        var role = _app.Role == AppRole.Host ? "主机" : "客户端";
        _statusItem.Text = $"状态：{role} · {_app.StatusText}";

        var host = _app.Role == AppRole.Host;
        _switchItem.Visible = host;
        _switchItem.Enabled = host && _app.IsConnected;
        _switchItem.Text = _app.HasControl ? "切回本机键鼠" : "切换到客户端";

        _clipboardItem.Enabled = _app.IsConnected;
        _testItem.Enabled = _app.MonitorEnabled;

        _hostRoleItem.Checked = host;
        _clientRoleItem.Checked = !host;
        _startupItem.Checked = Startup.IsEnabled;

        _notify.Text = $"OneDesk · {role} · {_app.StatusText}";
        _notify.Icon = GetIcon();

        _lastControlled = _app.HasControl;
        _lastConnected = _app.IsConnected;
        _lastBlocked = _app.InjectionBlocked;
    }

    private Icon GetIcon()
    {
        var controlled = _app.HasControl;
        var connected = _app.IsConnected;
        if (_iconData is { } existing && _iconControlled == controlled && _iconConnected == connected)
        {
            return existing.Icon;
        }

        if (_iconData is { } old) TrayIconFactory.Destroy(old);
        _iconData = TrayIconFactory.Create(controlled, connected);
        _iconControlled = controlled;
        _iconConnected = connected;
        return _iconData.Value.Icon;
    }

    private void ShowBalloon(string message, bool error)
    {
        if (_disposed) return;
        try
        {
            _notify.ShowBalloonTip(2500, error ? "OneDesk 出错了" : "OneDesk", message,
                error ? ToolTipIcon.Error : ToolTipIcon.Info);
        }
        catch
        {
            // notifications are best-effort
        }
    }

    private void ExitApp()
    {
        _timer.Stop();
        _notify.Visible = false;
        _app.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Dispose();
            _notify.Dispose();
            _menu.Dispose();
            _app.Notification -= OnNotification;
            if (_iconData is { } icon) TrayIconFactory.Destroy(icon);
            _iconData = null;
        }

        base.Dispose(disposing);
    }
}
