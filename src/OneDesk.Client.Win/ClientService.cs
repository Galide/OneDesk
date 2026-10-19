using System.Net.Sockets;
using OneDesk.Core;
using OneDesk.Platform.Win;
using Message = OneDesk.Core.Message;

namespace OneDesk.Client.Win;

/// <summary>
/// The Windows side of a Windows-to-Windows pair. It connects to the machine that owns the
/// keyboard and mouse, replays the forwarded events with SendInput, and switches the monitor
/// input back to itself when control is released.
/// </summary>
public sealed class ClientService : IDisposable
{
    private readonly object _gate = new();
    private readonly InputInjector _input = new();
    private volatile AppConfig _config;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _sessionCts;
    private Task? _loop;
    private SecureChannel? _channel;
    private volatile bool _connected;
    private volatile bool _hasControl;
    private string _lastError = "";
    private string _lastDdc = "";

    public ClientService(AppConfig config)
    {
        _config = config.Clone();
        _config.Normalize();
    }

    public event Action? StateChanged;

    public AppConfig Config => _config;
    public bool Connected => _connected;
    public bool HasControl => _hasControl;
    public bool InjectionBlocked => _input.Blocked;
    public string LastError => _lastError;
    public string DdcStatus => _lastDdc;

    public string StatusText
    {
        get
        {
            if (_hasControl) return "主机键鼠控制中";
            if (_connected) return "已连接主机";
            return "未连接主机";
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        Log.Info($"Windows 客户端启动，目标 {_config.PeerHost}:{_config.Port}");
    }

    public void RestartSession()
    {
        lock (_gate)
        {
            try
            {
                _sessionCts?.Cancel();
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>Persists the new configuration and reconnects with it.</summary>
    public void ApplyConfig(AppConfig config)
    {
        config.Role = AppRole.Client;
        config.Normalize();
        config.Save();

        var old = _config;
        if (_hasControl)
        {
            _hasControl = false;
            try
            {
                _input.ReleaseAll();
            }
            catch
            {
                // ignored
            }

            SwitchMonitor(old.Monitor.PeerInput, old);
        }

        _config = config.Clone();
        RestartSession();
        RaiseState();
        Log.Info("配置已更新，连接将重新建立");
    }

    public async Task<string> TestSwitchAsync(int input)
    {
        var cfg = _config;
        var (ok, message) = await Task.Run(() => Ddc.SetInput(cfg.Monitor.MonitorIndex,
            cfg.Monitor.MonitorNameContains, input)).ConfigureAwait(false);
        _lastDdc = message;
        if (!ok) _lastError = message;
        else _lastError = "";
        RaiseState();
        return message;
    }

    public async Task<string> ReadInputAsync()
    {
        var cfg = _config;
        var (value, message) = await Task.Run(() => Ddc.GetInput(cfg.Monitor.MonitorIndex,
            cfg.Monitor.MonitorNameContains)).ConfigureAwait(false);
        _lastDdc = message;
        RaiseState();
        return message;
    }

    public async Task<string> SendClipboardToHostAsync()
    {
        SecureChannel? channel;
        lock (_gate) channel = _channel;
        if (channel is null || !_connected)
        {
            return "主机未连接";
        }

        if (!_config.ClipboardSync)
        {
            return "剪贴板同步已在设置中关闭";
        }

        var text = WinClipboard.GetText();
        if (string.IsNullOrEmpty(text)) return "本机剪贴板为空";

        try
        {
            await channel.SendAsync(Message.Clipboard(text), CancellationToken.None).ConfigureAwait(false);
            return $"已发送 {text.Length} 个字符到主机";
        }
        catch (Exception ex)
        {
            return "发送失败: " + ex.Message;
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _config;
            if (!cfg.AutoConnect || string.IsNullOrWhiteSpace(cfg.PeerHost))
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunSessionAsync(cfg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                SetError("连接心跳超时，正在重连");
            }
            catch (SocketException ex)
            {
                SetError($"无法连接 {cfg.PeerHost}:{cfg.Port}（{ex.SocketErrorCode}）");
            }
            catch (InvalidDataException ex)
            {
                SetError("连接被拒绝：" + ex.Message + "（请检查两端配对密码）");
            }
            catch (IOException ex)
            {
                SetError("网络连接断开：" + ex.Message);
            }
            catch (Exception ex)
            {
                SetError("连接异常：" + ex.GetType().Name + ": " + ex.Message);
            }

            if (ct.IsCancellationRequested) break;
            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
    }

    private async Task RunSessionAsync(AppConfig cfg, CancellationToken outerCt)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        lock (_gate)
        {
            _sessionCts?.Dispose();
            _sessionCts = session;
        }

        SecureChannel? channel = null;
        try
        {
            using var tcp = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            connectCts.CancelAfter(Math.Max(1000, cfg.ConnectTimeoutMs));

            await tcp.ConnectAsync(cfg.PeerHost, cfg.Port, connectCts.Token).ConfigureAwait(false);
            tcp.NoDelay = true;
            var stream = tcp.GetStream();

            var salt = SecureChannel.NewSalt();
            await SecureChannel.WriteSaltAsync(stream, salt, connectCts.Token).ConfigureAwait(false);
            var key = SecureChannel.DeriveKey(cfg.Password, salt);
            channel = new SecureChannel(stream, key);

            await channel.SendAsync(Message.Hello(cfg.ClientName), connectCts.Token).ConfigureAwait(false);
            var ack = await channel.ReceiveAsync(connectCts.Token).ConfigureAwait(false);
            if (ack is null || ack.Type != MessageType.HelloAck)
            {
                throw new InvalidDataException("对方没有返回握手确认");
            }

            if (ack.Version != Protocol.Version)
            {
                throw new InvalidDataException(
                    $"协议版本不匹配：本机 {Protocol.Version}，主机 {ack.Version}");
            }

            lock (_gate) _channel = channel;
            _connected = true;
            _lastError = "";
            Log.Info($"已连接主机 {cfg.PeerHost}:{cfg.Port}");
            RaiseState();

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            while (!session.IsCancellationRequested)
            {
                readCts.CancelAfter(Math.Max(3000, cfg.HeartbeatTimeoutMs));
                var message = await channel.ReceiveAsync(readCts.Token).ConfigureAwait(false);
                if (message is null) break;
                await HandleMessageAsync(message, cfg, channel, session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!session.IsCancellationRequested)
        {
            throw new OperationCanceledException("heartbeat timeout");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_sessionCts, session)) _sessionCts = null;
            }

            var hadControl = _hasControl;
            _hasControl = false;
            _connected = false;
            lock (_gate)
            {
                if (ReferenceEquals(_channel, channel)) _channel = null;
            }

            if (hadControl)
            {
                try
                {
                    _input.ReleaseAll();
                }
                catch
                {
                    // ignored
                }

                Log.Warn("与主机的连接断开，正在把显示器切回主机");
                SwitchMonitor(cfg.Monitor.PeerInput, cfg);
            }

            if (channel is not null)
            {
                try
                {
                    channel.Dispose();
                }
                catch
                {
                    // ignored
                }
            }

            RaiseState();
            Log.Info("主机连接已断开");
        }
    }

    private async Task HandleMessageAsync(Message message, AppConfig cfg, SecureChannel channel, CancellationToken ct)
    {
        switch (message.Type)
        {
            case MessageType.Take:
                if (_hasControl) _input.ReleaseAll();
                _hasControl = true;
                Log.Info("主机已接管：开始注入键鼠");
                RaiseState();
                break;

            case MessageType.Release:
                if (_hasControl)
                {
                    _hasControl = false;
                    _input.ReleaseAll();
                    Log.Info("主机已释放控制，切回本机输入");

                    // Do not block the receive loop on DDC: the monitor can take a moment to react.
                    _ = Task.Run(() => SwitchMonitor(cfg.Monitor.PeerInput, cfg), CancellationToken.None);

                    if (cfg.ClipboardSync)
                    {
                        var text = WinClipboard.GetText();
                        if (!string.IsNullOrEmpty(text))
                        {
                            try
                            {
                                await channel.SendAsync(Message.Clipboard(text), ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Log.Warn("发送本机剪贴板失败: " + ex.Message);
                            }
                        }
                    }

                    try
                    {
                        await channel.SendAsync(new Message { Type = MessageType.ReleaseAck }, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignored
                    }

                    RaiseState();
                }

                break;

            case MessageType.Key:
                if (_hasControl) _input.Key(message.Vk, message.Scan, message.Flags);
                break;

            case MessageType.MouseMove:
                if (_hasControl) _input.MouseMove(message.Nx, message.Ny);
                break;

            case MessageType.MouseButton:
                if (_hasControl) _input.MouseButton(message.Button, message.Down, message.Nx, message.Ny);
                break;

            case MessageType.Wheel:
                if (_hasControl) _input.Wheel(message.Delta, message.HDelta, message.Nx, message.Ny);
                break;

            case MessageType.Clipboard:
                if (cfg.ClipboardSync && !string.IsNullOrEmpty(message.Text))
                {
                    var text = message.Text;
                    _ = Task.Run(() => WinClipboard.SetText(text));
                }

                break;

            case MessageType.Ping:
                await channel.SendAsync(new Message { Type = MessageType.Pong }, ct).ConfigureAwait(false);
                break;

            case MessageType.Pong:
                break;

            case MessageType.Error:
                SetError("主机返回错误: " + message.Text);
                break;
        }
    }

    private void SwitchMonitor(int input, AppConfig cfg)
    {
        if (!cfg.Monitor.Enabled) return;

        var (ok, message) = Ddc.SetInput(cfg.Monitor.MonitorIndex, cfg.Monitor.MonitorNameContains, input);
        _lastDdc = message;
        if (!ok)
        {
            _lastError = "显示器切换失败：" + message;
            Log.Warn(_lastError);
        }
        else
        {
            Log.Info(message);
        }

        RaiseState();
    }

    private void SetError(string message)
    {
        if (_lastError == message) return;
        _lastError = message;
        Log.Warn(message);
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

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        if (_loop is not null)
        {
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignored
            }
        }

        if (_hasControl)
        {
            try
            {
                _input.ReleaseAll();
                SwitchMonitor(_config.Monitor.PeerInput, _config);
            }
            catch
            {
                // ignored
            }
        }

        _input.Dispose();
        _cts?.Dispose();
        _sessionCts?.Dispose();
    }
}
