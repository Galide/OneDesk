using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using OneDesk.Core;
using OneDesk.Platform.Win;

namespace OneDesk.Client.Win;

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
            // The client receives input, so the indicator sits on the left of the screen.
            g.FillEllipse(accentBrush, 6f, 9f, 4f, 4f);
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
    private readonly ClientService _service;
    private readonly NotifyIcon _notify;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ConcurrentQueue<string> _notifications = new();
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _reconnectItem;
    private readonly ToolStripMenuItem _clipboardItem;
    private readonly ToolStripMenuItem _testItem;
    private readonly ToolStripMenuItem _startupItem;
    private SettingsForm? _settings;
    private (Icon Icon, IntPtr Handle)? _iconData;
    private bool? _lastControlled;
    private bool? _lastConnected;
    private bool _lastBlocked;
    private volatile bool _dirty = true;
    private bool _disposed;

    public TrayApp(ClientService service, bool openSettings)
    {
        _service = service;

        _statusItem = new ToolStripMenuItem("状态：启动中") { Enabled = false };

        _reconnectItem = new ToolStripMenuItem("重新连接");
        _reconnectItem.Click += (_, _) => _service.RestartSession();

        _clipboardItem = new ToolStripMenuItem("发送剪贴板到主机");
        _clipboardItem.Click += async (_, _) =>
        {
            try
            {
                var message = await _service.SendClipboardToHostAsync();
                _notifications.Enqueue(message);
            }
            catch (Exception ex)
            {
                _notifications.Enqueue("发送剪贴板失败: " + ex.Message);
            }
        };

        _testItem = new ToolStripMenuItem("测试显示器切换");
        _testItem.Click += async (_, _) => await TestMonitorAsync();

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => ShowSettings();

        var logItem = new ToolStripMenuItem("打开日志");
        logItem.Click += (_, _) => OpenLog();

        _startupItem = new ToolStripMenuItem("开机启动") { CheckOnClick = false };
        _startupItem.Click += (_, _) =>
        {
            Startup.SetEnabled(!Startup.IsEnabled);
            _startupItem.Checked = Startup.IsEnabled;
        };

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_reconnectItem);
        menu.Items.Add(_clipboardItem);
        menu.Items.Add(_testItem);
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
            Text = "OneDesk 客户端"
        };
        _notify.DoubleClick += (_, _) => ShowSettings();

        _service.StateChanged += () => _dirty = true;

        _timer = new System.Windows.Forms.Timer { Interval = 350 };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();

        UpdateState();

        if (openSettings)
        {
            _notifications.Enqueue("OneDesk 客户端已启动。请填写主机地址和配对密码。");
            ShowSettings();
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
                _notify.ShowBalloonTip(4000, "OneDesk 客户端", message, ToolTipIcon.Info);
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

        var connected = _service.Connected;
        var controlled = _service.HasControl;
        var blocked = _service.InjectionBlocked;

        _statusItem.Text = "状态：" + _service.StatusText +
                           (blocked ? " · 注入被拒绝" : "");
        _reconnectItem.Enabled = !string.IsNullOrWhiteSpace(_service.Config.PeerHost);
        _clipboardItem.Enabled = connected;
        _testItem.Enabled = _service.Config.Monitor.Enabled;
        _startupItem.Checked = Startup.IsEnabled;

        var text = "OneDesk 客户端 · " + _service.StatusText;
        _notify.Text = text.Length > 63 ? text[..63] : text;

        if (blocked && !_lastBlocked)
        {
            _notifications.Enqueue("输入注入被系统拒绝：前台窗口以管理员身份运行。请以管理员身份运行 OneDesk 客户端。");
        }

        _lastBlocked = blocked;

        if (_lastControlled == controlled && _lastConnected == connected && _iconData is not null) return;

        var old = _iconData;
        _iconData = TrayIconFactory.Create(controlled, connected);
        _notify.Icon = _iconData.Value.Icon;
        if (old is not null) TrayIconFactory.Destroy(old.Value);
        _lastControlled = controlled;
        _lastConnected = connected;
    }

    private async Task TestMonitorAsync()
    {
        var config = _service.Config;
        var confirm = MessageBox.Show(
            $"这会把显示器切到主机所在的接口（{Ddc.InputName(config.Monitor.PeerInput)}，" +
            $"{config.Monitor.PeerInput}），3 秒后再切回本机接口。\n" +
            "如果切换后黑屏，请用显示器上的按键切回。\n\n继续吗？",
            "测试显示器切换", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var first = await _service.TestSwitchAsync(config.Monitor.PeerInput);
            await Task.Delay(3000);
            var second = await _service.TestSwitchAsync(config.Monitor.MyInput);
            _notifications.Enqueue(first + "；3 秒后切回：" + second);
        }
        catch (Exception ex)
        {
            _notifications.Enqueue("测试失败: " + ex.Message);
        }
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
                MessageBox.Show("日志文件尚未创建。", "OneDesk 客户端", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            MessageBox.Show("无法打开日志: " + ex.Message, "OneDesk 客户端",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
