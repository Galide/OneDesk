using OneDesk.Core;
using OneDesk.Platform.Win;
using static OneDesk.Host.Native;

namespace OneDesk.App;

/// <summary>Single settings window for both roles, laid out like a macOS settings pane.</summary>
public sealed class SettingsForm : Form
{
    private const int CardWidth = 610;
    private const int InnerWidth = CardWidth - 36;

    private readonly AppHost _app;
    private AppConfig _config;
    private AppRole _role;
    private HotkeySpec _hotkey = new();
    private bool _loading;
    private bool _closing;
    private bool _refreshingMonitors;

    private readonly FlowLayoutPanel _content = new();
    private readonly Panel _footer = new();
    private readonly Panel _header = new();
    private readonly LogoBadge _logo = new();
    private readonly Label _titleLabel = new();
    private readonly Label _subtitleLabel = new();
    private readonly StatusPill _status = new();

    private readonly SegmentedControl _roleSegment = new();
    private readonly Label _roleHint = new();
    private readonly Card _roleCard = new();

    private readonly TextField _peerHost = new();
    private readonly TextField _password = new();
    private readonly TextField _portField = new();
    private readonly TextField _addressField = new();
    private readonly TextField _hotkeyField = new();
    private readonly SelectField _monitorSelect = new();
    private readonly SelectField _myInput = new();
    private readonly SelectField _peerInput = new();
    private readonly ToggleSwitch _autoSwitch = new();
    private readonly ToggleSwitch _clipboardToggle = new();
    private readonly ToggleSwitch _swapToggle = new();
    private readonly ToggleSwitch _autoConnectToggle = new();
    private readonly ToggleSwitch _startupToggle = new();

    private readonly FieldRow _peerHostRow;
    private readonly FieldRow _passwordRow;
    private readonly FieldRow _portRow;
    private readonly FieldRow _addressRow;
    private readonly FieldRow _monitorRow;
    private readonly FieldRow _myInputRow;
    private readonly FieldRow _peerInputRow;
    private readonly FieldRow _hotkeyRow;
    private readonly ActionRow _monitorActions;
    private readonly SwitchRow _autoSwitchRow;
    private readonly SwitchRow _clipboardRow;
    private readonly SwitchRow _swapRow;
    private readonly SwitchRow _autoConnectRow;
    private readonly SwitchRow _startupRow;
    private readonly TextRow _monitorStatus = new("", 40);

    private readonly Card _pairCard = new();
    private readonly Card _monitorCard = new();
    private readonly Card _hotkeyCard = new();
    private readonly Card _optionsCard = new();
    private readonly GroupLabel _pairGroup = new("配对");
    private readonly GroupLabel _monitorGroup = new("显示器");
    private readonly GroupLabel _hotkeyGroup = new("切换热键");
    private readonly GroupLabel _optionsGroup = new("选项");

    private readonly Label _hotkeyHint = new();
    private readonly Label _footerHint = new();
    private readonly ActionButton _copyPassword = new("复制") { Width = 60 };
    private readonly ActionButton _generatePassword = new("重新生成", ActionButton.ButtonKind.Ghost) { Width = 78 };
    private readonly ActionButton _copyAddress = new("复制") { Width = 60 };
    private readonly ActionButton _refreshMonitors = new("刷新") { Width = 60 };
    private readonly ActionButton _readInput = new("读取当前输入") { Width = 116 };
    private readonly ActionButton _testMonitor = new("测试切换") { Width = 92 };
    private readonly ActionButton _resetHotkey = new("默认", ActionButton.ButtonKind.Ghost) { Width = 60 };
    private readonly ActionButton _save = new("保存", ActionButton.ButtonKind.Primary) { Width = 88 };
    private readonly ActionButton _cancel = new("取消") { Width = 80 };

    private readonly List<(Card Card, SettingRow[] Rows)> _stacks = new();

    public SettingsForm(AppHost app)
    {
        _app = app;
        _config = app.Config.Clone();
        _role = _config.Role;
        _hotkey = _config.Hotkey;

        _peerHostRow = new FieldRow("主机地址", _peerHost);
        _peerHostRow.Hint = new Label { Text = "运行「主机」角色的那台电脑的 IPv4 地址。" };
        _passwordRow = new FieldRow("配对密码", _password, _copyPassword, _generatePassword);
        _portRow = new FieldRow("端口", _portField);
        _addressRow = new FieldRow("本机地址", _addressField, _copyAddress);
        _monitorRow = new FieldRow("显示器", _monitorSelect, _refreshMonitors);
        _myInputRow = new FieldRow("本机接口", _myInput);
        _peerInputRow = new FieldRow("对端接口", _peerInput);
        _monitorActions = new ActionRow(_readInput, _testMonitor);
        _hotkeyRow = new FieldRow("切换热键", _hotkeyField, _resetHotkey) { Hint = _hotkeyHint };
        _hotkeyHint.Text = "点击输入框后按下新的组合键";
        _autoSwitchRow = new SwitchRow("自动切换显示器输入", "切换控制时通过 DDC/CI 改变显示器的输入源", _autoSwitch);
        _clipboardRow = new SwitchRow("同步剪贴板", "切换时把本机剪贴板文本发送到对端", _clipboardToggle);
        _swapRow = new SwitchRow("交换 Ctrl 与 Command", "按 Mac 的习惯把 Ctrl 映射为 Command", _swapToggle);
        _autoConnectRow = new SwitchRow("启动后自动连接", "开机后自动连接主机，断线时持续重试", _autoConnectToggle);
        _startupRow = new SwitchRow("开机自动启动", "登录 Windows 后自动在后台运行", _startupToggle);

        BuildWindow();
        BuildContent();
        LoadValues();
        WireEvents();
        ApplyRole();
    }

    private void BuildWindow()
    {
        Text = "OneDesk 设置";
        ClientSize = new Size(690, 840);
        MinimumSize = new Size(690, 560);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Window;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;

        _content.Dock = DockStyle.Fill;
        _content.FlowDirection = FlowDirection.TopDown;
        _content.WrapContents = false;
        _content.AutoScroll = true;
        _content.BackColor = Theme.Window;
        _content.Padding = new Padding(30, 22, 30, 24);
        Controls.Add(_content);

        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 72;
        _footer.BackColor = Theme.Window;
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };
        Controls.Add(_footer);

        _footerHint.AutoSize = false;
        _footerHint.Font = Theme.Small;
        _footerHint.ForeColor = Theme.Secondary;
        _footerHint.BackColor = Theme.Window;
        _footerHint.SetBounds(30, 24, 400, 26);
        _footer.Controls.Add(_footerHint);

        _cancel.BackColor = Theme.Window;
        _save.BackColor = Theme.Window;
        _footer.Controls.Add(_cancel);
        _footer.Controls.Add(_save);
        Resize += (_, _) => LayoutFooter();
    }

    private void LayoutFooter()
    {
        var saveX = _footer.Width - 30 - _save.Width;
        _save.Location = new Point(saveX, 19);
        _cancel.Location = new Point(saveX - 10 - _cancel.Width, 19);
    }

    private void BuildContent()
    {
        // Header.
        _header.Width = CardWidth;
        _header.Height = 76;
        _header.Margin = new Padding(0, 0, 0, 18);
        _header.BackColor = Theme.Window;

        _logo.Location = new Point(0, 4);
        _titleLabel.Text = "OneDesk";
        _titleLabel.Font = Theme.Title;
        _titleLabel.ForeColor = Theme.Text;
        _titleLabel.AutoSize = true;
        _titleLabel.BackColor = Color.Transparent;
        _titleLabel.Location = new Point(60, 2);

        _subtitleLabel.Text = "一套键盘鼠标，控制两台电脑";
        _subtitleLabel.Font = Theme.Small;
        _subtitleLabel.ForeColor = Theme.Secondary;
        _subtitleLabel.AutoSize = true;
        _subtitleLabel.BackColor = Color.Transparent;
        _subtitleLabel.Location = new Point(62, 32);

        _status.BackColor = Theme.Window;
        _status.SetStatus("启动中", Theme.Tertiary);
        _status.Location = new Point(CardWidth - _status.Width, 18);

        _header.Controls.AddRange(new Control[] { _logo, _titleLabel, _subtitleLabel, _status });
        _content.Controls.Add(_header);

        // Role.
        _content.Controls.Add(_roleGroupLabel());
        _roleCard.Width = CardWidth;
        _roleCard.Height = 114;
        _roleCard.Margin = new Padding(0, 0, 0, 18);
        _roleSegment.Location = new Point(18, 16);
        _roleSegment.Width = InnerWidth;
        _roleSegment.Items = new[] { "主机", "客户端" };
        _roleHint.AutoSize = false;
        _roleHint.Font = Theme.Small;
        _roleHint.ForeColor = Theme.Secondary;
        _roleHint.BackColor = Color.Transparent;
        _roleHint.SetBounds(18, 64, InnerWidth, 38);
        _roleCard.Controls.AddRange(new Control[] { _roleSegment, _roleHint });
        _content.Controls.Add(_roleCard);

        // Pairing.
        _pairGroup.Margin = new Padding(6, 0, 0, 6);
        _content.Controls.Add(_pairGroup);
        ConfigureCard(_pairCard);
        AddRows(_pairCard, _peerHostRow, _passwordRow, _portRow, _addressRow);

        // Monitor.
        _monitorGroup.Margin = new Padding(6, 0, 0, 6);
        _content.Controls.Add(_monitorGroup);
        ConfigureCard(_monitorCard);
        AddRows(_monitorCard, _autoSwitchRow, _monitorRow, _myInputRow, _peerInputRow, _monitorActions, _monitorStatus);

        // Hotkey.
        _hotkeyGroup.Margin = new Padding(6, 0, 0, 6);
        _content.Controls.Add(_hotkeyGroup);
        ConfigureCard(_hotkeyCard);
        AddRows(_hotkeyCard, _hotkeyRow);

        // Options.
        _optionsGroup.Margin = new Padding(6, 0, 0, 6);
        _content.Controls.Add(_optionsGroup);
        ConfigureCard(_optionsCard);
        AddRows(_optionsCard, _clipboardRow, _swapRow, _autoConnectRow, _startupRow);
    }

    private static GroupLabel _roleGroupLabel()
    {
        var label = new GroupLabel("使用方式");
        label.Margin = new Padding(6, 0, 0, 6);
        return label;
    }

    private void ConfigureCard(Card card)
    {
        card.Width = CardWidth;
        card.Margin = new Padding(0, 0, 0, 18);
        _content.Controls.Add(card);
    }

    private void AddRows(Card card, params SettingRow[] rows)
    {
        foreach (var row in rows) card.Controls.Add(row);
        _stacks.Add((card, rows));
    }

    private void Relayout()
    {
        foreach (var (card, rows) in _stacks)
        {
            var visible = rows.Where(r => r.RowVisible).ToArray();
            var y = card.Padding.Top;
            for (var i = 0; i < visible.Length; i++)
            {
                visible[i].ShowSeparator = i < visible.Length - 1;
                visible[i].SetBounds(card.Padding.Left, y, card.Width - card.Padding.Horizontal, visible[i].Height);
                y += visible[i].Height;
            }

            card.Height = y + card.Padding.Bottom;
        }

        _content.PerformLayout();
    }

    private void WireEvents()
    {
        _roleSegment.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            _role = _roleSegment.SelectedIndex == 0 ? AppRole.Host : AppRole.Client;
            ApplyRole();
        };

        _copyPassword.Click += (_, _) => Copy(_password.Text, "密码已复制");
        _generatePassword.Click += (_, _) =>
        {
            _password.Text = GroupPassword(AppConfig.GeneratePassword());
            SetHint("已生成新的配对密码，记得在对端填写同一个密码。");
        };

        _copyAddress.Click += (_, _) => Copy(_addressField.Text, "本机地址已复制");
        _peerHost.TextChanged += (_, _) =>
        {
            if (!_loading) SetHint("");
        };

        _refreshMonitors.Click += async (_, _) => await RefreshMonitorsAsync();
        _readInput.Click += async (_, _) => await ReadInputAsync();
        _testMonitor.Click += async (_, _) => await TestMonitorAsync();

        _myInput.SelectionChanged += (_, _) => HandleCustomInput(_myInput);
        _peerInput.SelectionChanged += (_, _) => HandleCustomInput(_peerInput);

        _hotkeyField.KeyDown += OnHotkeyKeyDown;
        _resetHotkey.Click += (_, _) =>
        {
            _hotkey = new HotkeySpec();
            _hotkeyField.Text = _hotkey.Describe();
            SetHint("已恢复默认热键。");
        };

        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => Close();

        _app.StateChanged += OnAppStateChanged;
        FormClosed += (_, _) =>
        {
            _closing = true;
            _app.StateChanged -= OnAppStateChanged;
        };

        Shown += (_, _) =>
        {
            LayoutFooter();
            UpdateStatus();
        };
        Resize += (_, _) => UpdateStatus();
    }

    private void OnAppStateChanged()
    {
        if (_closing || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(UpdateStatus));
        }
        catch
        {
            // the window is closing
        }
    }

    private void LoadValues()
    {
        _loading = true;
        _config = _app.Config.Clone();
        _role = _app.Config.Role;
        _hotkey = _config.Hotkey;

        _roleSegment.SelectedIndex = _role == AppRole.Host ? 0 : 1;
        _peerHost.Text = _config.PeerHost;
        _password.Text = _config.GroupedPassword();
        _portField.Text = _config.Port.ToString();
        _addressField.Text = AddressText();
        _addressField.ReadOnly = true;
        _autoSwitch.Checked = _config.Monitor.Enabled;
        _clipboardToggle.Checked = _config.ClipboardSync;
        _swapToggle.Checked = _config.SwapCtrlCmd;
        _autoConnectToggle.Checked = _config.AutoConnect;
        _startupToggle.Checked = Startup.IsEnabled;
        _hotkeyField.Text = _hotkey.Describe();
        _hotkeyField.ReadOnly = true;
        _hotkeyField.Inner.ShortcutsEnabled = false;
        FillInputSelect(_myInput, _config.Monitor.MyInput);
        FillInputSelect(_peerInput, _config.Monitor.PeerInput);
        _monitorSelect.SetItems(new[] { new SelectField.Item("正在检测显示器…", null) }, 0);
        _monitorStatus.Text = "正在检测支持 DDC/CI 的显示器…";
        _footerHint.Text = "";
        _loading = false;
    }

    /// <summary>Re-reads the configuration, e.g. after the tray switched the role.</summary>
    public void ReloadFromApp()
    {
        if (_closing) return;
        LoadValues();
        ApplyRole();
        UpdateStatus();
    }

    private void ApplyRole()
    {
        var host = _role == AppRole.Host;

        _peerHostRow.RowVisible = !host;
        _addressRow.RowVisible = host;
        _hotkeyGroup.Visible = host;
        _hotkeyCard.Visible = host;
        _swapRow.RowVisible = host;
        _autoConnectRow.RowVisible = !host;
        _passwordRow.SetTrailingVisible(_generatePassword, host);

        _roleHint.Text = host
            ? "本机连接键盘和鼠标，另一台电脑运行同一个 OneDesk 并选择「客户端」。"
            : "本机不接键盘鼠标。填写主机地址、端口和配对密码，由主机推送键鼠事件。";

        Relayout();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_closing) return;

        if (_role != _app.Role)
        {
            _status.SetStatus("切换角色后需保存", Theme.Orange);
        }
        else if (_app.InjectionBlocked)
        {
            _status.SetStatus("注入被系统阻止", Theme.Red);
        }
        else
        {
            var accent = _app.HasControl ? Theme.Green : _app.IsConnected ? Theme.Accent : Theme.Tertiary;
            _status.SetStatus(_app.StatusText, accent);
        }

        _status.Location = new Point(_header.Width - _status.Width, 18);
    }

    private static string AddressText()
    {
        var addresses = AppConfig.LocalIPv4Addresses();
        return addresses.Count == 0 ? "未获取到 IPv4 地址" : string.Join(" · ", addresses);
    }

    private static string GroupPassword(string password)
    {
        var p = AppConfig.CanonicalizePassword(password);
        if (p.Length != 16) return p;
        return string.Join("-", Enumerable.Range(0, 4).Select(i => p.Substring(i * 4, 4)));
    }

    private static void FillInputSelect(SelectField select, int value)
    {
        var items = AppConfig.InputPresets
            .Select(p => new SelectField.Item($"{p.Name} ({p.Value})", p.Value))
            .ToList();
        items.Add(new SelectField.Item("自定义…", null));

        var index = items.FindIndex(i => i.Tag is int v && v == value);
        if (index < 0)
        {
            items.Insert(items.Count - 1, new SelectField.Item($"自定义 ({value})", value));
            index = items.Count - 2;
        }

        select.SetItems(items, index);
    }

    private static int InputValue(SelectField select) => select.Selected?.Tag is int value ? value : 0;

    private void HandleCustomInput(SelectField select)
    {
        if (_loading || select.Selected?.Tag is int) return;

        var current = select == _myInput ? _config.Monitor.MyInput : _config.Monitor.PeerInput;
        var text = UiDialog.Prompt(this, "自定义输入值",
            "输入显示器输入源的 VCP 0x60 值（0-255）。多数显示器使用 15/16（DP）或 17/18（HDMI）。",
            current.ToString());

        if (text is not null && int.TryParse(text, out var value) && value is >= 0 and <= 255)
        {
            FillInputSelect(select, value);
            SetHint($"已把输入值设为 {value}。");
        }
        else
        {
            FillInputSelect(select, current);
        }
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
            _hotkeyField.Text = "请再按一个非修饰键";
            return;
        }

        var mods = e.Modifiers;
        var ctrl = (mods & Keys.Control) != 0;
        var alt = (mods & Keys.Alt) != 0;
        var shift = (mods & Keys.Shift) != 0;
        var win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

        if (!ctrl && !alt && !shift && !win)
        {
            _hotkeyField.Text = _hotkey.Describe();
            SetHint("热键至少要包含一个修饰键（Ctrl / Alt / Shift / Win）。");
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
        _hotkeyField.Text = _hotkey.Describe();
        SetHint("热键已更新，保存后生效。");
    }

    private async Task RefreshMonitorsAsync()
    {
        if (_refreshingMonitors) return;
        _refreshingMonitors = true;
        _refreshMonitors.Enabled = false;
        _monitorSelect.SetItems(new[] { new SelectField.Item("正在检测…", null) }, 0);

        try
        {
            var monitors = await Task.Run(() => Ddc.ListMonitors(true));
            if (monitors.Count == 0)
            {
                _monitorSelect.SetItems(new[] { new SelectField.Item("未检测到支持 DDC/CI 的显示器", null) }, 0);
                _monitorStatus.Text = "没有找到显示器。请确认显示器已打开，且 DDC/CI 未被禁用。";
            }
            else
            {
                var items = monitors.Select(m => new SelectField.Item(m.Label, m)).ToList();
                var index = monitors.FindIndex(m => m.Index == _config.Monitor.MonitorIndex);
                _monitorSelect.SetItems(items, index < 0 ? 0 : index);
                _monitorStatus.Text = $"检测到 {monitors.Count} 个显示器。切换输入源前请确认接口值。";
            }
        }
        catch (Exception ex)
        {
            _monitorStatus.Text = "检测失败：" + ex.Message;
        }
        finally
        {
            _refreshMonitors.Enabled = true;
            _refreshingMonitors = false;
        }
    }

    private async Task ReadInputAsync()
    {
        SetHint("正在读取显示器输入…");
        try
        {
            _monitorStatus.Text = await _app.ReadMonitorInputAsync();
        }
        catch (Exception ex)
        {
            _monitorStatus.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            SetHint("");
        }
    }

    private async Task TestMonitorAsync()
    {
        var name = Ddc.InputName(InputValue(_peerInput));
        var peer = _role == AppRole.Host ? "对端" : "主机";
        if (!UiDialog.Confirm(this, "测试切换显示器",
                $"显示器会切换到{peer}接口（{name}），3 秒后自动切回。\n\n如果画面没有回来，请在对端手动切回。",
                "开始测试"))
        {
            return;
        }

        SetHint("正在测试显示器切换…");
        _readInput.Enabled = false;
        _testMonitor.Enabled = false;
        try
        {
            _monitorStatus.Text = await _app.TestMonitorAsync();
        }
        catch (Exception ex)
        {
            _monitorStatus.Text = "测试失败：" + ex.Message;
        }
        finally
        {
            _readInput.Enabled = true;
            _testMonitor.Enabled = true;
            SetHint("");
        }
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(_portField.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            SetHint("端口需要是 1024–65535 之间的数字。");
            _portField.Focus();
            return;
        }

        var password = AppConfig.CanonicalizePassword(_password.Text);
        if (password.Length < 8)
        {
            SetHint("配对密码至少需要 8 个字符。");
            _password.Focus();
            return;
        }

        var peerHost = _peerHost.Text.Trim();
        if (_role == AppRole.Client && peerHost.Length == 0)
        {
            SetHint("客户端需要填写主机的 IPv4 地址。");
            _peerHost.Focus();
            return;
        }

        var config = _config.Clone();
        config.Role = _role;
        config.PeerHost = peerHost;
        config.Port = port;
        config.Password = password;
        config.Hotkey = _hotkey;
        config.Monitor.Enabled = _autoSwitch.Checked;
        if (_monitorSelect.Selected?.Tag is MonitorInfo monitor)
        {
            config.Monitor.MonitorIndex = monitor.Index;
            config.Monitor.MonitorNameContains = "";
        }

        config.Monitor.MyInput = InputValue(_myInput);
        config.Monitor.PeerInput = InputValue(_peerInput);
        config.ClipboardSync = _clipboardToggle.Checked;
        config.SwapCtrlCmd = _swapToggle.Checked;
        config.AutoConnect = _autoConnectToggle.Checked;
        config.StartAutomatically = _startupToggle.Checked;

        _save.Enabled = false;
        SetHint("正在保存…");
        try
        {
            if (!await _app.ApplyConfigAsync(config))
            {
                SetHint("保存失败，详情见日志。");
                return;
            }

            Startup.SetEnabled(config.StartAutomatically);
            _hotkey = config.Hotkey;
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            _save.Enabled = true;
        }
    }

    private void Copy(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            SetHint(message);
        }
        catch (Exception ex)
        {
            SetHint("复制失败：" + ex.Message);
        }
    }

    private void SetHint(string text)
    {
        _footerHint.Text = text;
        _footerHint.ForeColor = text.Length == 0 ? Theme.Secondary : Theme.Secondary;
    }
}
