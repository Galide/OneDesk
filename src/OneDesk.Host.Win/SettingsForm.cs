using System.Drawing;
using System.Windows.Forms;
using OneDesk.Core;
using OneDesk.Platform.Win;

namespace OneDesk.Host;

public sealed class SettingsForm : Form
{
    private readonly HostService _service;
    private readonly AppConfig _config;
    private HotkeySpec _hotkey;

    private readonly TextBox _password = new();
    private readonly NumericUpDown _port = new();
    private readonly TextBox _hotkeyBox = new();
    private readonly CheckBox _monitorEnabled = new();
    private readonly ComboBox _monitorCombo = new();
    private readonly ComboBox _myInput = new();
    private readonly ComboBox _peerInput = new();
    private readonly CheckBox _clipboard = new();
    private readonly CheckBox _swap = new();
    private readonly CheckBox _startup = new();
    private readonly Label _hint = new();
    private readonly Label _monitorStatus = new();
    private readonly Button _refreshMonitors = new();
    private readonly Button _readInput = new();
    private readonly Button _testMonitor = new();
    private readonly Button _save = new();
    private bool _refreshingMonitors;

    public SettingsForm(HostService service)
    {
        _service = service;
        _config = service.Config.Clone();
        _hotkey = CopyHotkey(_config.Hotkey);

        BuildUi();
        LoadValues();
        Shown += async (_, _) => await RefreshMonitorsAsync();
    }

    private static HotkeySpec CopyHotkey(HotkeySpec source) => new()
    {
        Ctrl = source.Ctrl,
        Alt = source.Alt,
        Shift = source.Shift,
        Win = source.Win,
        VirtualKey = source.VirtualKey
    };

    private static Label MakeLabel(string text, int x, int y, int width = 0)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = width == 0,
            Location = new Point(x, y),
            ForeColor = Color.FromArgb(60, 66, 78)
        };
        if (width > 0) label.Size = new Size(width, 22);
        return label;
    }

    private void BuildUi()
    {
        Text = "OneDesk 设置";
        ClientSize = new Size(620, 716);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(248, 249, 252);

        var pairGroup = new GroupBox
        {
            Text = "配对",
            Location = new Point(12, 10),
            Size = new Size(596, 158)
        };

        pairGroup.Controls.Add(MakeLabel("配对密码", 18, 32));
        _password.Location = new Point(110, 29);
        _password.Size = new Size(280, 27);
        _password.Font = new Font("Consolas", 10F);
        pairGroup.Controls.Add(_password);

        var copyPassword = new Button
        {
            Text = "复制",
            Location = new Point(398, 28),
            Size = new Size(76, 28)
        };
        copyPassword.Click += (_, _) => CopyPassword();
        pairGroup.Controls.Add(copyPassword);

        var regenerate = new Button
        {
            Text = "重新生成",
            Location = new Point(482, 28),
            Size = new Size(96, 28)
        };
        regenerate.Click += (_, _) =>
        {
            _password.Text = GroupPassword(AppConfig.GeneratePassword());
            _hint.Text = "新密码已生成。请在 Mac 端网页中填入相同密码。";
        };
        pairGroup.Controls.Add(regenerate);

        pairGroup.Controls.Add(MakeLabel("本机 IPv4", 18, 72));
        var ips = new TextBox
        {
            ReadOnly = true,
            Location = new Point(110, 69),
            Size = new Size(468, 27),
            BackColor = Color.White
        };
        var addresses = AppConfig.LocalIPv4Addresses();
        ips.Text = addresses.Count > 0 ? string.Join("   ", addresses) : "未检测到局域网地址";
        pairGroup.Controls.Add(ips);

        pairGroup.Controls.Add(MakeLabel("监听端口", 18, 110));
        _port.Location = new Point(110, 107);
        _port.Size = new Size(100, 27);
        _port.Minimum = 1024;
        _port.Maximum = 65535;
        pairGroup.Controls.Add(_port);
        pairGroup.Controls.Add(MakeLabel("在 Mac 端网页中填入上面的 IP，端口保持一致", 222, 112));

        Controls.Add(pairGroup);

        var hotkeyGroup = new GroupBox
        {
            Text = "快捷键",
            Location = new Point(12, 178),
            Size = new Size(596, 82)
        };
        hotkeyGroup.Controls.Add(MakeLabel("切换热键", 18, 34));
        _hotkeyBox.Location = new Point(110, 31);
        _hotkeyBox.Size = new Size(260, 27);
        _hotkeyBox.ReadOnly = true;
        _hotkeyBox.BackColor = Color.White;
        _hotkeyBox.ShortcutsEnabled = false;
        _hotkeyBox.KeyDown += OnHotkeyKeyDown;
        hotkeyGroup.Controls.Add(_hotkeyBox);
        hotkeyGroup.Controls.Add(MakeLabel("点击输入框后直接按下组合键", 384, 36));
        Controls.Add(hotkeyGroup);

        var monitorGroup = new GroupBox
        {
            Text = "显示器输入切换（DDC/CI）",
            Location = new Point(12, 268),
            Size = new Size(596, 262)
        };

        _monitorEnabled.Text = "切换时自动改变显示器输入源";
        _monitorEnabled.Location = new Point(18, 26);
        _monitorEnabled.AutoSize = true;
        monitorGroup.Controls.Add(_monitorEnabled);

        monitorGroup.Controls.Add(MakeLabel("显示器", 18, 63));
        _monitorCombo.Location = new Point(110, 60);
        _monitorCombo.Size = new Size(382, 27);
        _monitorCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        monitorGroup.Controls.Add(_monitorCombo);

        _refreshMonitors.Text = "刷新";
        _refreshMonitors.Location = new Point(500, 59);
        _refreshMonitors.Size = new Size(78, 28);
        _refreshMonitors.Click += async (_, _) => await RefreshMonitorsAsync();
        monitorGroup.Controls.Add(_refreshMonitors);

        monitorGroup.Controls.Add(MakeLabel("本机接口", 18, 102));
        _myInput.Location = new Point(110, 99);
        _myInput.Size = new Size(150, 27);
        _myInput.DropDownStyle = ComboBoxStyle.DropDown;
        monitorGroup.Controls.Add(_myInput);

        monitorGroup.Controls.Add(MakeLabel("Mac 接口", 282, 102));
        _peerInput.Location = new Point(370, 99);
        _peerInput.Size = new Size(208, 27);
        _peerInput.DropDownStyle = ComboBoxStyle.DropDown;
        monitorGroup.Controls.Add(_peerInput);

        _readInput.Text = "读取当前输入";
        _readInput.Location = new Point(110, 140);
        _readInput.Size = new Size(118, 30);
        _readInput.Click += async (_, _) => await ReadInputAsync();
        monitorGroup.Controls.Add(_readInput);

        _testMonitor.Text = "测试切到 Mac（3 秒后切回）";
        _testMonitor.Location = new Point(238, 140);
        _testMonitor.Size = new Size(210, 30);
        _testMonitor.Click += async (_, _) => await TestMonitorAsync();
        monitorGroup.Controls.Add(_testMonitor);

        _monitorStatus.Location = new Point(18, 180);
        _monitorStatus.Size = new Size(560, 42);
        _monitorStatus.ForeColor = Color.FromArgb(80, 88, 102);
        monitorGroup.Controls.Add(_monitorStatus);

        monitorGroup.Controls.Add(MakeLabel(
            "切换由当前正在显示的机器发起，这样 DDC 命令才能到达显示器。\nMac 端使用 m1ddc 切回 Windows。",
            18, 222, 560));
        Controls.Add(monitorGroup);

        var optionsGroup = new GroupBox
        {
            Text = "选项",
            Location = new Point(12, 542),
            Size = new Size(596, 110)
        };
        _clipboard.Text = "切换时同步剪贴板（纯文本，最大约 1 MB）";
        _clipboard.Location = new Point(18, 24);
        _clipboard.AutoSize = true;
        optionsGroup.Controls.Add(_clipboard);

        _swap.Text = "把 Ctrl 映射为 Command，Win 映射为 Control（适合 Mac 快捷键习惯）";
        _swap.Location = new Point(18, 54);
        _swap.AutoSize = true;
        optionsGroup.Controls.Add(_swap);

        _startup.Text = "登录 Windows 时自动启动";
        _startup.Location = new Point(18, 84);
        _startup.AutoSize = true;
        optionsGroup.Controls.Add(_startup);
        Controls.Add(optionsGroup);

        _hint.Location = new Point(14, 660);
        _hint.Size = new Size(390, 44);
        _hint.ForeColor = Color.FromArgb(100, 108, 122);
        Controls.Add(_hint);

        _save.Text = "保存";
        _save.Location = new Point(410, 668);
        _save.Size = new Size(94, 32);
        _save.Click += async (_, _) => await SaveAsync();
        Controls.Add(_save);

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(514, 668),
            Size = new Size(88, 32),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(cancel);
        CancelButton = cancel;

        _hint.Text = "Mac 端安装后在浏览器打开它自己的 http://127.0.0.1:47822 进行配置。";
    }

    private void LoadValues()
    {
        _password.Text = GroupPassword(_config.Password);
        _port.Value = Math.Clamp(_config.Port, 1024, 65535);
        _hotkeyBox.Text = _hotkey.Describe();
        _monitorEnabled.Checked = _config.Monitor.Enabled;
        _clipboard.Checked = _config.ClipboardSync;
        _swap.Checked = _config.SwapCtrlCmd;
        _startup.Checked = Startup.IsEnabled || _config.StartAutomatically;

        FillInputCombo(_myInput);
        FillInputCombo(_peerInput);
        SelectInput(_myInput, _config.Monitor.MyInput);
        SelectInput(_peerInput, _config.Monitor.PeerInput);
    }

    private static string GroupPassword(string password)
    {
        var p = AppConfig.CanonicalizePassword(password);
        if (p.Length != 16) return p;
        return string.Join("-", Enumerable.Range(0, 4).Select(i => p.Substring(i * 4, 4)));
    }

    private static void FillInputCombo(ComboBox combo)
    {
        combo.Items.Clear();
        foreach (var (name, value) in AppConfig.InputPresets)
        {
            combo.Items.Add($"{name} ({value})");
        }
    }

    private static void SelectInput(ComboBox combo, int value)
    {
        var label = AppConfig.InputPresets.FirstOrDefault(p => p.Value == value).Name;
        combo.Text = label is null ? value.ToString() : $"{label} ({value})";
    }

    private static int ParseInput(ComboBox combo)
    {
        var text = combo.Text.Trim();
        if (int.TryParse(text, out var direct) && direct is >= 0 and <= 255) return direct;
        var open = text.LastIndexOf('(');
        var close = text.LastIndexOf(')');
        if (open >= 0 && close > open && int.TryParse(text[(open + 1)..close], out var value) &&
            value is >= 0 and <= 255)
        {
            return value;
        }

        return -1;
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin)
        {
            _hotkeyBox.Text = "请再按一个非修饰键…";
            return;
        }

        var mods = e.Modifiers;
        var ctrl = (mods & Keys.Control) != 0;
        var alt = (mods & Keys.Alt) != 0;
        var shift = (mods & Keys.Shift) != 0;
        var win = (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                  (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0;

        if (!ctrl && !alt && !shift && !win)
        {
            _hint.Text = "热键至少需要一个修饰键（Ctrl / Alt / Shift），避免影响正常打字。";
            return;
        }

        _hotkey = new HotkeySpec
        {
            Ctrl = ctrl,
            Alt = alt,
            Shift = shift,
            Win = win,
            VirtualKey = (int)key
        };
        _hotkeyBox.Text = _hotkey.Describe();
        _hint.Text = "热键已更新，保存后生效。";
    }

    private async Task RefreshMonitorsAsync()
    {
        if (_refreshingMonitors) return;
        _refreshingMonitors = true;
        _refreshMonitors.Enabled = false;
        _monitorCombo.Items.Clear();
        _monitorCombo.Items.Add("正在检测…");
        _monitorCombo.SelectedIndex = 0;
        try
        {
            var monitors = await Task.Run(() => Ddc.ListMonitors(true));
            _monitorCombo.Items.Clear();
            if (monitors.Count == 0)
            {
                _monitorCombo.Items.Add("未检测到支持 DDC/CI 的显示器");
                _monitorCombo.SelectedIndex = 0;
                _monitorStatus.Text = "没有找到物理显示器。请确认显示器已连接并支持 DDC/CI。";
            }
            else
            {
                foreach (var monitor in monitors) _monitorCombo.Items.Add(monitor);
                var index = Math.Clamp(_config.Monitor.MonitorIndex, 0, monitors.Count - 1);
                _monitorCombo.SelectedIndex = index;
                _monitorStatus.Text = $"检测到 {monitors.Count} 个显示器。";
            }
        }
        catch (Exception ex)
        {
            _monitorStatus.Text = "检测显示器失败：" + ex.Message;
        }
        finally
        {
            _refreshMonitors.Enabled = true;
            _refreshingMonitors = false;
        }
    }

    private async Task ReadInputAsync()
    {
        _readInput.Enabled = false;
        try
        {
            _monitorStatus.Text = "正在读取…";
            _monitorStatus.Text = await _service.ReadMonitorInputAsync();
        }
        finally
        {
            _readInput.Enabled = true;
        }
    }

    private async Task TestMonitorAsync()
    {
        var confirm = MessageBox.Show(
            "这会把显示器立即切换到 Mac 输入。\n3 秒后 Windows 会尝试切回；如果失败，请用 Mac 端网页切回。\n\n继续吗？",
            "测试显示器切换", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _testMonitor.Enabled = false;
        Cursor = Cursors.WaitCursor;
        try
        {
            _monitorStatus.Text = "正在切换…";
            _monitorStatus.Text = await _service.TestMonitorAsync();
        }
        finally
        {
            _testMonitor.Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    private void CopyPassword()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_password.Text))
            {
                _password.Text = GroupPassword(AppConfig.GeneratePassword());
            }

            Clipboard.SetText(_password.Text);
            _hint.Text = "配对密码已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            _hint.Text = "复制失败：" + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        var myInput = ParseInput(_myInput);
        var peerInput = ParseInput(_peerInput);
        if (myInput < 0 || peerInput < 0)
        {
            MessageBox.Show("显示器输入值无效。请选择预设或输入 0-255 的数字。", "OneDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var password = AppConfig.CanonicalizePassword(_password.Text);
        if (password.Length < 8)
        {
            MessageBox.Show("配对密码至少需要 8 个字符。", "OneDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selectedMonitor = _monitorCombo.SelectedItem as MonitorInfo;
        var config = _config.Clone();
        config.Role = AppRole.Host;
        config.Password = password;
        config.Port = (int)_port.Value;
        config.Hotkey = _hotkey;
        config.Monitor.Enabled = _monitorEnabled.Checked;
        if (selectedMonitor is not null)
        {
            config.Monitor.MonitorIndex = selectedMonitor.Index;
            config.Monitor.MonitorNameContains = "";
        }

        config.Monitor.MyInput = myInput;
        config.Monitor.PeerInput = peerInput;
        config.ClipboardSync = _clipboard.Checked;
        config.SwapCtrlCmd = _swap.Checked;
        config.StartAutomatically = _startup.Checked;

        _save.Enabled = false;
        try
        {
            Startup.SetEnabled(config.StartAutomatically);
            if (!await _service.ApplyConfigAsync(config))
            {
                MessageBox.Show("配置未能应用，请查看提示或日志后重试。", "OneDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "OneDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _save.Enabled = true;
        }
    }
}
