using System.Drawing;
using System.Windows.Forms;
using OneDesk.Core;
using OneDesk.Platform.Win;

namespace OneDesk.Client.Win;

public sealed class SettingsForm : Form
{
    private readonly ClientService _service;
    private readonly AppConfig _config;

    private readonly TextBox _peerHost = new();
    private readonly NumericUpDown _port = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _monitorEnabled = new();
    private readonly ComboBox _monitorCombo = new();
    private readonly ComboBox _myInput = new();
    private readonly ComboBox _peerInput = new();
    private readonly CheckBox _clipboard = new();
    private readonly CheckBox _autoConnect = new();
    private readonly CheckBox _startup = new();
    private readonly Label _hint = new();
    private readonly Label _monitorStatus = new();
    private readonly Button _refreshMonitors = new();
    private readonly Button _readInput = new();
    private readonly Button _testMonitor = new();
    private readonly Button _save = new();
    private bool _refreshingMonitors;

    public SettingsForm(ClientService service)
    {
        _service = service;
        _config = service.Config.Clone();

        BuildUi();
        LoadValues();
        Shown += async (_, _) => await RefreshMonitorsAsync();
    }

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
        Text = "OneDesk 客户端设置";
        ClientSize = new Size(620, 620);
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
            Text = "主机",
            Location = new Point(12, 10),
            Size = new Size(596, 158)
        };

        pairGroup.Controls.Add(MakeLabel("主机地址", 18, 32));
        _peerHost.Location = new Point(110, 29);
        _peerHost.Size = new Size(240, 27);
        pairGroup.Controls.Add(_peerHost);

        pairGroup.Controls.Add(MakeLabel("端口", 366, 32));
        _port.Location = new Point(414, 29);
        _port.Size = new Size(100, 27);
        _port.Minimum = 1024;
        _port.Maximum = 65535;
        pairGroup.Controls.Add(_port);

        pairGroup.Controls.Add(MakeLabel("配对密码", 18, 70));
        _password.Location = new Point(110, 67);
        _password.Size = new Size(280, 27);
        _password.Font = new Font("Consolas", 10F);
        pairGroup.Controls.Add(_password);

        var copyPassword = new Button
        {
            Text = "复制",
            Location = new Point(398, 66),
            Size = new Size(116, 28)
        };
        copyPassword.Click += (_, _) => CopyPassword();
        pairGroup.Controls.Add(copyPassword);

        pairGroup.Controls.Add(MakeLabel(
            "主机地址、端口和密码必须与另一台机器上的 OneDesk 完全一致。\n" +
            "密码中的空格和「-」会被忽略，大小写不敏感。",
            18, 104, 560));
        Controls.Add(pairGroup);

        var monitorGroup = new GroupBox
        {
            Text = "显示器输入切换（DDC/CI）",
            Location = new Point(12, 178),
            Size = new Size(596, 252)
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

        monitorGroup.Controls.Add(MakeLabel("主机接口", 282, 102));
        _peerInput.Location = new Point(370, 99);
        _peerInput.Size = new Size(208, 27);
        _peerInput.DropDownStyle = ComboBoxStyle.DropDown;
        monitorGroup.Controls.Add(_peerInput);

        _readInput.Text = "读取当前输入";
        _readInput.Location = new Point(110, 140);
        _readInput.Size = new Size(118, 30);
        _readInput.Click += async (_, _) => await ReadInputAsync();
        monitorGroup.Controls.Add(_readInput);

        _testMonitor.Text = "测试切到主机（3 秒后切回）";
        _testMonitor.Location = new Point(238, 140);
        _testMonitor.Size = new Size(210, 30);
        _testMonitor.Click += async (_, _) => await TestMonitorAsync();
        monitorGroup.Controls.Add(_testMonitor);

        _monitorStatus.Location = new Point(18, 180);
        _monitorStatus.Size = new Size(560, 56);
        _monitorStatus.ForeColor = Color.FromArgb(80, 88, 102);
        monitorGroup.Controls.Add(_monitorStatus);

        monitorGroup.Controls.Add(MakeLabel(
            "「本机接口」是本机接在显示器的哪个口，「主机接口」是另一台机器接的哪个口。\n" +
            "切换由正在显示的那一方发起，这样 DDC 命令才能到达显示器。",
            18, 222, 560));
        Controls.Add(monitorGroup);

        var optionsGroup = new GroupBox
        {
            Text = "选项",
            Location = new Point(12, 440),
            Size = new Size(596, 110)
        };

        _clipboard.Text = "切换时同步剪贴板（纯文本，最大约 1 MB）";
        _clipboard.Location = new Point(18, 24);
        _clipboard.AutoSize = true;
        optionsGroup.Controls.Add(_clipboard);

        _autoConnect.Text = "启动后自动连接主机";
        _autoConnect.Location = new Point(18, 54);
        _autoConnect.AutoSize = true;
        optionsGroup.Controls.Add(_autoConnect);

        _startup.Text = "登录 Windows 时自动启动";
        _startup.Location = new Point(18, 84);
        _startup.AutoSize = true;
        optionsGroup.Controls.Add(_startup);
        Controls.Add(optionsGroup);

        _hint.Location = new Point(14, 558);
        _hint.Size = new Size(390, 44);
        _hint.ForeColor = Color.FromArgb(100, 108, 122);
        Controls.Add(_hint);

        _save.Text = "保存";
        _save.Location = new Point(410, 566);
        _save.Size = new Size(94, 32);
        _save.Click += (_, _) => SaveAsync();
        Controls.Add(_save);

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(514, 566),
            Size = new Size(88, 32),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(cancel);
        CancelButton = cancel;

        _hint.Text = "在主机（有键盘鼠标的那台机器）上打开 OneDesk，把它的地址、端口和密码填到上面。";
    }

    private void LoadValues()
    {
        _peerHost.Text = _config.PeerHost;
        _port.Value = Math.Clamp(_config.Port, 1024, 65535);
        _password.Text = GroupPassword(_config.Password);
        _monitorEnabled.Checked = _config.Monitor.Enabled;
        _clipboard.Checked = _config.ClipboardSync;
        _autoConnect.Checked = _config.AutoConnect;
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
            _monitorStatus.Text = await _service.ReadInputAsync();
        }
        finally
        {
            _readInput.Enabled = true;
        }
    }

    private async Task TestMonitorAsync()
    {
        var peerInput = ParseInput(_peerInput);
        if (peerInput < 0)
        {
            MessageBox.Show("请先填写有效的「主机接口」值。", "OneDesk 客户端",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var myInput = ParseInput(_myInput);
        var confirm = MessageBox.Show(
            $"这会把显示器切到主机所在的接口（{peerInput}），3 秒后再切回本机接口（{myInput}）。\n" +
            "如果切换后黑屏，请用显示器上的按键切回。\n\n继续吗？",
            "测试显示器切换", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _testMonitor.Enabled = false;
        Cursor = Cursors.WaitCursor;
        try
        {
            _monitorStatus.Text = "正在切换…";
            var first = await _service.TestSwitchAsync(peerInput);
            await Task.Delay(3000);
            var second = myInput >= 0 ? await _service.TestSwitchAsync(myInput) : "未设置本机接口，未切回";
            _monitorStatus.Text = first + "；3 秒后切回：" + second;
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

    private void SaveAsync()
    {
        var host = _peerHost.Text.Trim();
        if (_autoConnect.Checked && host.Length == 0)
        {
            MessageBox.Show("请填写主机地址，或关闭「启动后自动连接主机」。", "OneDesk 客户端",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var myInput = ParseInput(_myInput);
        var peerInput = ParseInput(_peerInput);
        if (myInput < 0 || peerInput < 0)
        {
            MessageBox.Show("显示器输入值无效。请选择预设或输入 0-255 的数字。", "OneDesk 客户端",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var password = AppConfig.CanonicalizePassword(_password.Text);
        if (password.Length < 8)
        {
            MessageBox.Show("配对密码至少需要 8 个字符。", "OneDesk 客户端",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selectedMonitor = _monitorCombo.SelectedItem as MonitorInfo;
        var config = _config.Clone();
        config.Role = AppRole.Client;
        config.PeerHost = host;
        config.Port = (int)_port.Value;
        config.Password = password;
        config.Monitor.Enabled = _monitorEnabled.Checked;
        if (selectedMonitor is not null)
        {
            config.Monitor.MonitorIndex = selectedMonitor.Index;
            config.Monitor.MonitorNameContains = "";
        }

        config.Monitor.MyInput = myInput;
        config.Monitor.PeerInput = peerInput;
        config.ClipboardSync = _clipboard.Checked;
        config.AutoConnect = _autoConnect.Checked;
        config.StartAutomatically = _startup.Checked;

        _save.Enabled = false;
        try
        {
            Startup.SetEnabled(config.StartAutomatically);
            _service.ApplyConfig(config);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "OneDesk 客户端",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _save.Enabled = true;
        }
    }
}
