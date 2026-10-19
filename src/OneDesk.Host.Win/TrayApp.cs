using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using OneDesk.Core;

namespace OneDesk.Host;

internal static class TrayIconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static (Icon Icon, IntPtr Handle) Create(bool remote, bool connected)
    {
        var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var accent = remote
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

public sealed class TrayApp : ApplicationContext
{
    private readonly HostService _service;
    private readonly NotifyIcon _notify;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ConcurrentQueue<string> _notifications = new();
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _switchItem;
    private readonly ToolStripMenuItem _clipboardItem;
    private readonly ToolStripMenuItem _startupItem;
    private SettingsForm? _settings;
    private (Icon Icon, IntPtr Handle)? _iconData;
    private bool? _lastRemote;
    private bool? _lastConnected;
    private volatile bool _dirty = true;
    private bool _disposed;

    public TrayApp(HostService service, bool firstRun)
    {
        _service = service;

        _statusItem = new ToolStripMenuItem("状态：启动中") { Enabled = false };
        _switchItem = new ToolStripMenuItem("切换到 Mac");
        _switchItem.Click += (_, _) => _ = Task.Run(_service.ToggleAsyncPublic);

        _clipboardItem = new ToolStripMenuItem("发送剪贴板到 Mac");
        _clipboardItem.Click += async (_, _) =>
        {
            try
            {
                await _service.SendClipboardNowAsync();
            }
            catch (Exception ex)
            {
                _notifications.Enqueue("发送剪贴板失败: " + ex.Message);
            }
        };

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => ShowSettings();

        var logItem = new ToolStripMenuItem("打开日志");
        logItem.Click += (_, _) => OpenLog();

        _startupItem = new ToolStripMenuItem("开机启动") { CheckOnClick = false };
        _startupItem.Click += (_, _) =>
        {
            var enable = !Startup.IsEnabled;
            Startup.SetEnabled(enable);
            _startupItem.Checked = Startup.IsEnabled;
        };

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_switchItem);
        menu.Items.Add(_clipboardItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(logItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notify = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "OneDesk"
        };
        _notify.DoubleClick += (_, _) => ShowSettings();

        _service.StateChanged += () => _dirty = true;
        _service.Notification += (message, _) => _notifications.Enqueue(message);

        _timer = new System.Windows.Forms.Timer { Interval = 350 };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();

        UpdateState();

        if (firstRun)
        {
            _notifications.Enqueue("OneDesk 已启动。请设置配对密码，并在 Mac 端填入本机 IP。");
            ShowSettings();
        }

        if (!_service.HookRunning)
        {
            _notifications.Enqueue("无法安装全局键盘/鼠标钩子，请检查安全软件或将 OneDesk 加入白名单。");
        }

        if (!_service.IsListening)
        {
            _notifications.Enqueue("无法监听端口：" + _service.LastError);
        }
    }

    private void Pump()
    {
        if (_dirty)
        {
            _dirty = false;
            UpdateState();
        }

        while (_notifications.TryDequeue(out var message))
        {
            try
            {
                _notify.ShowBalloonTip(4000, "OneDesk", message, ToolTipIcon.Info);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void UpdateState()
    {
        if (_disposed) return;

        var connected = _service.IsConnected;
        var remote = _service.IsRemote;
        _statusItem.Text = "状态：" + _service.StatusText +
                           (_service.HookRunning ? "" : " · 输入钩子失败") +
                           (_service.IsListening ? "" : " · 端口监听失败");
        _switchItem.Text = remote ? "切回 Windows" : "切换到 Mac";
        _switchItem.ShortcutKeyDisplayString = _service.Config.Hotkey.Describe();
        _switchItem.Enabled = connected || remote;
        _clipboardItem.Enabled = connected;
        _startupItem.Checked = Startup.IsEnabled;

        var text = "OneDesk · " + _service.StatusText;
        _notify.Text = text.Length > 63 ? text[..63] : text;

        if (_lastRemote == remote && _lastConnected == connected && _iconData is not null) return;

        var old = _iconData;
        _iconData = TrayIconFactory.Create(remote, connected);
        _notify.Icon = _iconData.Value.Icon;
        if (old is not null) TrayIconFactory.Destroy(old.Value);
        _lastRemote = remote;
        _lastConnected = connected;
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            if (_settings.WindowState == FormWindowState.Minimized)
            {
                _settings.WindowState = FormWindowState.Normal;
            }

            _settings.Activate();
            return;
        }

        _settings = new SettingsForm(_service);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void OpenLog()
    {
        try
        {
            var path = Log.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show("日志文件尚未创建。", "OneDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开日志: " + ex.Message, "OneDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExitApp()
    {
        _disposed = true;
        _timer.Stop();
        _notify.Visible = false;
        _service.Dispose();
        _notify.Dispose();
        if (_iconData is not null) TrayIconFactory.Destroy(_iconData.Value);
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Dispose();
            _notify.Dispose();
            if (_iconData is not null) TrayIconFactory.Destroy(_iconData.Value);
        }

        base.Dispose(disposing);
    }
}
