using OneDesk.Client;
using OneDesk.Core;
using OneDesk.Host;

namespace OneDesk.App;

/// <summary>
/// Owns whichever service the current role needs (host or client) and swaps it when the role
/// changes. The tray icon and the settings window only ever talk to this class.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly object _gate = new();
    private HostService? _host;
    private ClientService? _client;
    private AppConfig _config;
    private bool _disposed;

    public AppHost(AppConfig config)
    {
        _config = config.Clone();
        _config.Normalize();
    }

    public event Action? StateChanged;
    public event Action<string, bool>? Notification;

    public AppConfig Config => _config;
    public AppRole Role => _config.Role;

    public bool IsConnected => _host?.IsConnected ?? _client?.Connected ?? false;
    public bool HasControl => _host?.IsRemote ?? _client?.HasControl ?? false;
    public string LastError => _host?.LastError ?? _client?.LastError ?? "";
    public bool InjectionBlocked => _client?.InjectionBlocked ?? false;
    public bool HookRunning => _config.Role == AppRole.Host && (_host?.HookRunning ?? false);
    public bool IsListening => _config.Role == AppRole.Host && (_host?.IsListening ?? false);
    public bool MonitorEnabled => _config.Monitor.Enabled;

    public string StatusText
    {
        get
        {
            if (_config.Role == AppRole.Host)
            {
                if (_host is null) return "未启动";
                if (_host.IsRemote) return "客户端控制中";
                if (_host.IsConnected) return "已连接客户端 · 本机键鼠";
                return "等待客户端连接";
            }

            if (_client is null) return "未启动";
            if (_client.HasControl) return "主机键鼠控制中";
            return _client.Connected ? "已连接主机" : "未连接主机";
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_host is not null || _client is not null) return;

            if (_config.Role == AppRole.Host)
            {
                var service = new HostService(_config);
                service.StateChanged += RaiseState;
                service.Notification += OnNotification;
                _host = service;
            }
            else
            {
                var service = new ClientService(_config);
                service.StateChanged += RaiseState;
                _client = service;
            }
        }

        if (_config.Role == AppRole.Host) _host!.Start();
        else _client!.Start();
    }

    public void Stop()
    {
        HostService? host;
        ClientService? client;
        lock (_gate)
        {
            host = _host;
            client = _client;
            _host = null;
            _client = null;
        }

        host?.Dispose();
        client?.Dispose();
    }

    /// <summary>Persists the configuration and restarts the service, swapping roles if needed.</summary>
    public async Task<bool> ApplyConfigAsync(AppConfig config)
    {
        config.Normalize();
        var roleChanged = config.Role != _config.Role;

        if (!roleChanged)
        {
            if (_host is not null)
            {
                var applied = await _host.ApplyConfigAsync(config).ConfigureAwait(true);
                if (applied) _config = config.Clone();
                return applied;
            }

            if (_client is not null)
            {
                _client.ApplyConfig(config);
                _config = config.Clone();
                RaiseState();
                return true;
            }

            return false;
        }

        try
        {
            config.Save();
        }
        catch (Exception ex)
        {
            OnNotification("保存配置失败: " + ex.Message, true);
            return false;
        }

        Stop();
        _config = config.Clone();
        Start();
        RaiseState();
        Log.Info($"角色已切换为 {(config.Role == AppRole.Host ? "主机" : "客户端")}");
        return true;
    }

    public async Task ToggleAsync()
    {
        var host = _host;
        if (host is not null) await host.ToggleAsyncPublic().ConfigureAwait(false);
    }

    public async Task<string> SendClipboardAsync()
    {
        if (_host is not null)
        {
            await _host.SendClipboardNowAsync().ConfigureAwait(false);
            return "已发送剪贴板到客户端";
        }

        return _client is null ? "尚未启动" : await _client.SendClipboardToHostAsync().ConfigureAwait(false);
    }

    public async Task<string> TestMonitorAsync()
    {
        if (_host is not null) return await _host.TestMonitorAsync().ConfigureAwait(false);
        if (_client is not null) return await _client.TestMonitorAsync().ConfigureAwait(false);
        return "尚未启动";
    }

    public async Task<string> ReadMonitorInputAsync()
    {
        if (_host is not null) return await _host.ReadMonitorInputAsync().ConfigureAwait(false);
        if (_client is not null) return await _client.ReadInputAsync().ConfigureAwait(false);
        return "尚未启动";
    }

    private void OnNotification(string message, bool isError)
    {
        try
        {
            Notification?.Invoke(message, isError);
        }
        catch
        {
            // ignored
        }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
