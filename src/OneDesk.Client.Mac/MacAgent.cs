using OneDesk.Core;

namespace OneDesk.Client;

/// <summary>
/// The macOS app is either the machine that owns the keyboard and mouse (host) or the one that
/// is driven remotely (client). This facade owns whichever service the configuration asks for,
/// so the web panel and the daemon do not care which role is active.
/// </summary>
public sealed class MacAgent : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly MacDdc _ddc = new();
    private AppConfig _config;
    private MacClient? _client;
    private MacHost? _host;
    private bool _disposed;

    public event Action? StateChanged;

    public MacAgent(AppConfig config)
    {
        _config = config.Clone();
        _config.Normalize();
    }

    public AppConfig Config
    {
        get
        {
            lock (_gate) return _config.Clone();
        }
    }

    public AppRole Role
    {
        get
        {
            lock (_gate) return _config.Role;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StartCore();
        }
    }

    private void StartCore()
    {
        _config.Normalize();
        if (_config.Role == AppRole.Host)
        {
            var host = new MacHost(_config);
            host.StateChanged += OnStateChanged;
            _host = host;
            host.Start();
        }
        else
        {
            var client = new MacClient(_config);
            client.StateChanged += OnStateChanged;
            _client = client;
            MacPermissions.Request();
            client.Start();
        }
    }

    private void StopCore()
    {
        if (_host is not null)
        {
            _host.StateChanged -= OnStateChanged;
            try
            {
                _host.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("停止主机服务失败: " + ex.Message);
            }

            _host = null;
        }

        if (_client is not null)
        {
            _client.StateChanged -= OnStateChanged;
            try
            {
                _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warn("停止客户端服务失败: " + ex.Message);
            }

            _client = null;
        }
    }

    /// <summary>Saves the configuration and restarts the service when the role or network changed.</summary>
    public string? Save(AppConfig config)
    {
        config.Normalize();
        if (config.Port is < 1024 or > 65535) return "端口无效";
        if (config.Password.Length < 8) return "配对密码至少需要 8 个字符";
        if (config.Role == AppRole.Client && config.AutoConnect &&
            string.IsNullOrWhiteSpace(config.PeerHost))
        {
            return "客户端模式需要填写对端主机地址";
        }

        lock (_gate)
        {
            if (_disposed) return "正在退出";

            StopCore();
            try
            {
                config.Save();
            }
            catch (Exception ex)
            {
                StartCore();
                return "保存失败: " + ex.Message;
            }

            _config = config.Clone();
            StartCore();
        }

        Log.Info("配置已保存，当前角色: " + (config.Role == AppRole.Host ? "主机" : "客户端"));
        RaiseState();
        return null;
    }

    public string RunAction(string name)
    {
        try
        {
            switch (name)
            {
                case "reconnect":
                    lock (_gate)
                    {
                        if (_host is not null) _host.RestartSession();
                        else _client?.RestartSession();
                    }

                    return "正在重新连接…";

                case "toggle":
                {
                    MacHost? host;
                    lock (_gate) host = _host;
                    if (host is null) return "只有主机模式可以主动切换";
                    host.ToggleAsync().GetAwaiter().GetResult();
                    return host.IsRemote ? "已切换到对端控制" : "已切回本机控制";
                }

                case "test-my":
                    return TestSwitch(Config.Monitor.MyInput);

                case "test-peer":
                {
                    var cfg = Config;
                    var first = TestSwitch(cfg.Monitor.PeerInput);
                    Thread.Sleep(3000);
                    var second = TestSwitch(cfg.Monitor.MyInput);
                    return first + "；3 秒后切回：" + second;
                }

                case "sync-clipboard":
                    return SendClipboardAsync().GetAwaiter().GetResult();

                case "open-accessibility":
                    MacPermissions.OpenSettings();
                    return "已打开系统设置";

                default:
                    return "未知操作";
            }
        }
        catch (Exception ex)
        {
            return "操作失败: " + ex.Message;
        }
    }

    private string TestSwitch(int input)
    {
        MacClient? client;
        MacHost? host;
        lock (_gate)
        {
            client = _client;
            host = _host;
        }

        if (host is not null) return host.TestSwitchAsync(input).GetAwaiter().GetResult();
        if (client is not null) return client.TestSwitchAsync(input).GetAwaiter().GetResult();
        return "服务未启动";
    }

    private async Task<string> SendClipboardAsync()
    {
        MacClient? client;
        MacHost? host;
        lock (_gate)
        {
            client = _client;
            host = _host;
        }

        if (host is not null) return await host.SendClipboardNowAsync().ConfigureAwait(false);
        if (client is not null) return await client.SendClipboardToHostAsync().ConfigureAwait(false);
        return "服务未启动";
    }

    public object BuildStatus()
    {
        AppConfig cfg;
        MacClient? client;
        MacHost? host;
        lock (_gate)
        {
            cfg = _config;
            client = _client;
            host = _host;
        }

        var controlling = host?.IsRemote ?? false;
        var controlled = client?.HasControl ?? false;
        var connected = host?.IsConnected ?? client?.Connected ?? false;

        return new
        {
            hostname = Environment.MachineName,
            role = cfg.Role.ToString(),
            connected,
            controlling,
            controlled,
            control = controlling ? "out" : controlled ? "in" : "local",
            accessibility = MacPermissions.HasAccess,
            trusted = MacCapture.Trusted,
            capturing = host?.CaptureRunning ?? false,
            listening = host?.IsListening ?? false,
            autoConnect = cfg.AutoConnect,
            display = client?.DisplayLabel ?? "—",
            ddc = host?.DdcStatus ?? client?.DdcStatus ?? "",
            lastError = host?.LastError ?? client?.LastError ?? "",
            peer = cfg.PeerHost,
            hotkey = cfg.Hotkey.Describe(),
            addresses = AppConfig.LocalIPv4Addresses(),
            configPath = AppConfig.ConfigPath,
            m1ddc = _ddc.Available,
            m1ddcPath = _ddc.Path
        };
    }

    private void OnStateChanged()
    {
        RaiseState();
    }

    private void RaiseState()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
            // ignored
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            StopCore();
        }

        return ValueTask.CompletedTask;
    }
}
