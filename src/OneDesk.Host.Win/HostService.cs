using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OneDesk.Core;
using OneDesk.Platform.Win;
using static OneDesk.Host.Native;
using Message = OneDesk.Core.Message;

namespace OneDesk.Host;

/// <summary>
/// Windows side: owns the physical keyboard/mouse, the TCP listener, monitor switching
/// and the local clipboard.
/// </summary>
public sealed class HostService : IDisposable
{
    private readonly object _gate = new();
    private readonly InputHook _hook = new();
    private readonly ConcurrentQueue<Message> _outbox = new();
    private readonly ConcurrentDictionary<int, (int Scan, bool Extended)> _pressedKeys = new();
    private readonly ConcurrentDictionary<int, byte> _pressedButtons = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private volatile AppConfig _config;
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptTask;
    private SecureChannel? _channel;
    private CancellationTokenSource? _sessionCts;
    private volatile bool _connected;
    private volatile bool _remote;
    private long _lastMessageTicks;
    private bool _hotkeyLatch;
    private int _savedX;
    private int _savedY;
    private float _mouseNx;
    private float _mouseNy;
    private int _mouseDirty;
    private string _lastError = "";
    private bool _disposed;

    public event Action? StateChanged;
    public event Action<string, bool>? Notification;

    public HostService(AppConfig config)
    {
        _config = config.Clone();
        _config.Normalize();
        _hook.KeyHandler = OnKey;
        _hook.MouseHandler = OnMouse;
    }

    public AppConfig Config => _config;
    public bool IsRemote => _remote;
    public bool IsConnected => _connected;
    public bool HookRunning => _hook.IsRunning;
    public bool IsListening => _listener is not null;
    public string LastError => _lastError;

    public string StatusText
    {
        get
        {
            if (_remote) return "Mac 控制中";
            if (_connected) return "已连接 Mac · Windows 本地";
            return "等待 Mac 连接";
        }
    }

    public void Start()
    {
        if (!_hook.Start())
        {
            _lastError = "无法安装全局键盘/鼠标钩子";
            Log.Error(_lastError);
        }

        StartListener();
        Log.Info($"Windows Host 启动，热键 {_config.Hotkey.Describe()}");
    }

    private bool StartListener()
    {
        try
        {
            var cts = new CancellationTokenSource();
            var listener = new TcpListener(IPAddress.Any, _config.Port);
            listener.Start();
            _listenerCts = cts;
            _listener = listener;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
            Log.Info($"监听 0.0.0.0:{_config.Port}，本机地址: {string.Join(", ", AppConfig.LocalIPv4Addresses())}");
            return true;
        }
        catch (Exception ex)
        {
            _lastError = "监听端口失败: " + ex.Message;
            Log.Error(_lastError);
            NotifyError(_lastError);
            return false;
        }
    }

    private async Task StopListenerAsync()
    {
        var cts = _listenerCts;
        _listenerCts = null;
        cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // ignored
        }

        _listener = null;
        CloseSession();

        var accept = _acceptTask;
        _acceptTask = null;
        if (accept is not null)
        {
            try
            {
                await accept.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        cts?.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Warn("接受连接失败: " + ex.Message);
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken listenerCt)
    {
        using var tcpClient = client;
        SecureChannel? channel = null;
        CancellationTokenSource? sessionCts = null;
        var gotGate = false;
        var wasRemote = false;
        var current = false;

        try
        {
            if (!_connectionGate.Wait(0))
            {
                Log.Warn("拒绝额外连接：已有客户端");
                return;
            }

            gotGate = true;
            client.NoDelay = true;

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(listenerCt);
            handshake.CancelAfter(TimeSpan.FromSeconds(8));
            var stream = client.GetStream();

            var salt = await SecureChannel.ReadSaltAsync(stream, handshake.Token).ConfigureAwait(false);
            var key = SecureChannel.DeriveKey(_config.Password, salt);
            channel = new SecureChannel(stream, key);

            var hello = await channel.ReceiveAsync(handshake.Token).ConfigureAwait(false);
            if (hello is null || hello.Type != MessageType.Hello)
            {
                Log.Warn("握手失败：没有收到 Hello");
                return;
            }

            if (hello.Version != Protocol.Version)
            {
                try
                {
                    await channel.SendAsync(new Message
                    {
                        Type = MessageType.Error,
                        Text = $"协议版本不匹配: 本机 {Protocol.Version}, 对端 {hello.Version}"
                    }, handshake.Token).ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }

                Log.Warn($"协议版本不匹配: {hello.Version}");
                return;
            }

            sessionCts = new CancellationTokenSource();
            lock (_gate)
            {
                if (_channel is not null)
                {
                    Log.Warn("拒绝连接：已有活动会话");
                    return;
                }

                _channel = channel;
                _sessionCts = sessionCts;
                _connected = true;
                _lastMessageTicks = Environment.TickCount64;
            }

            Log.Info($"Mac 已连接: {hello.Name} ({client.Client.RemoteEndPoint})");
            await channel.SendAsync(new Message
            {
                Type = MessageType.HelloAck,
                Version = Protocol.Version,
                Name = Environment.MachineName
            }, handshake.Token).ConfigureAwait(false);

            RaiseState();
            var ct = sessionCts.Token;
            var receive = ReceiveLoopAsync(channel, ct);
            var send = SendLoopAsync(channel, ct);
            var heartbeat = HeartbeatLoopAsync(channel, ct);

            await Task.WhenAny(receive, send, heartbeat).ConfigureAwait(false);
            sessionCts.Cancel();
            try
            {
                await Task.WhenAll(receive, send, heartbeat).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // expected when one loop fails
            }
        }
        catch (OperationCanceledException)
        {
            // timeout or shutdown
        }
        catch (InvalidDataException ex)
        {
            Log.Warn("连接认证/协议失败: " + ex.Message);
        }
        catch (IOException ex)
        {
            if (!listenerCt.IsCancellationRequested) Log.Warn("连接 I/O 错误: " + ex.Message);
        }
        catch (Exception ex)
        {
            if (!listenerCt.IsCancellationRequested) Log.Warn("连接异常: " + ex.Message);
        }
        finally
        {
            sessionCts?.Cancel();

            lock (_gate)
            {
                current = ReferenceEquals(_channel, channel) && channel is not null;
                if (current)
                {
                    _channel = null;
                    _sessionCts = null;
                    _connected = false;
                }
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

            if (current && _remote)
            {
                wasRemote = true;
                _remote = false;
                ClearPressed();
                RestoreCursor();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    var (ok, msg) = Ddc.SetInput(_config.Monitor.MonitorIndex,
                        _config.Monitor.MonitorNameContains, _config.Monitor.MyInput);
                    if (!ok) Log.Warn("断线后尝试切回本机输入失败（Mac 端也会尝试）: " + msg);
                });
            }

            if (gotGate) _connectionGate.Release();
            sessionCts?.Dispose();

            if (current)
            {
                Log.Info(wasRemote ? "Mac 连接断开，已回到本地控制" : "Mac 连接已断开");
                RaiseState();
            }
        }
    }

    private async Task ReceiveLoopAsync(SecureChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (message is null) break;
            _lastMessageTicks = Environment.TickCount64;

            switch (message.Type)
            {
                case MessageType.Ping:
                    Enqueue(new Message { Type = MessageType.Pong });
                    break;
                case MessageType.Pong:
                    break;
                case MessageType.ReleaseAck:
                    break;
                case MessageType.Clipboard:
                    if (!_remote && _config.ClipboardSync && !string.IsNullOrEmpty(message.Text))
                    {
                        var text = message.Text;
                        _ = Task.Run(() => WinClipboard.SetText(text));
                        Log.Info($"已从 Mac 接收剪贴板（{text.Length} 字符）");
                    }
                    break;
                case MessageType.Error:
                    Log.Warn("Mac 返回错误: " + message.Text);
                    break;
            }
        }
    }

    private async Task SendLoopAsync(SecureChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            while (_outbox.TryDequeue(out var message))
            {
                await channel.SendAsync(message, ct).ConfigureAwait(false);
            }

            if (Interlocked.Exchange(ref _mouseDirty, 0) == 1)
            {
                await channel.SendAsync(Message.MouseMove(_mouseNx, _mouseNy), ct).ConfigureAwait(false);
            }

            await Task.Delay(4, ct).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatLoopAsync(SecureChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var idle = Environment.TickCount64 - _lastMessageTicks;
            if (idle > _config.HeartbeatTimeoutMs)
            {
                Log.Warn($"Mac 心跳超时（{idle} ms），断开连接");
                try
                {
                    channel.Dispose();
                }
                catch
                {
                    // ignored
                }

                return;
            }

            await channel.SendAsync(new Message { Type = MessageType.Ping }, ct).ConfigureAwait(false);
        }
    }

    private bool OnKey(HookKeyEvent e)
    {
        var cfg = _config;

        if (!e.Injected)
        {
            var ctrl = IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3);
            var alt = IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5);
            var shift = IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1);
            var win = IsDown(0x5B) || IsDown(0x5C);

            if (e.Vk == cfg.Hotkey.VirtualKey && cfg.Hotkey.Matches(ctrl, alt, shift, win, e.Vk))
            {
                if (e.Down)
                {
                    if (!_hotkeyLatch)
                    {
                        _hotkeyLatch = true;
                        _ = Task.Run(ToggleAsync);
                    }
                }
                else
                {
                    _hotkeyLatch = false;
                }

                return true;
            }
        }

        if (!_remote) return false;
        if (e.Injected) return false;

        if (e.Down)
        {
            var repeat = _pressedKeys.ContainsKey(e.Vk);
            if (!repeat) _pressedKeys[e.Vk] = (e.Scan, e.Extended);
            var flags = KeyFlags.Down | (e.Extended ? KeyFlags.Extended : 0) |
                        (repeat ? KeyFlags.Repeat : 0);
            Enqueue(Message.Key(e.Vk, e.Scan, flags));
        }
        else
        {
            if (_pressedKeys.TryRemove(e.Vk, out var info))
            {
                Enqueue(Message.Key(e.Vk, info.Scan, info.Extended ? KeyFlags.Extended : 0));
            }
        }

        return true;
    }

    private bool OnMouse(HookMouseEvent e)
    {
        if (!_remote || e.Injected) return false;

        var (nx, ny) = Normalize(e.X, e.Y);
        switch (e.Kind)
        {
            case HookMouseKind.Move:
                _mouseNx = nx;
                _mouseNy = ny;
                Interlocked.Exchange(ref _mouseDirty, 1);
                return false; // allow the local cursor to move; the screen is showing the Mac anyway
            case HookMouseKind.LeftDown:
                _pressedButtons.TryAdd(1, 0);
                Enqueue(Message.MouseButton(1, true, nx, ny));
                return true;
            case HookMouseKind.LeftUp:
                _pressedButtons.TryRemove(1, out _);
                Enqueue(Message.MouseButton(1, false, nx, ny));
                return true;
            case HookMouseKind.RightDown:
                _pressedButtons.TryAdd(2, 0);
                Enqueue(Message.MouseButton(2, true, nx, ny));
                return true;
            case HookMouseKind.RightUp:
                _pressedButtons.TryRemove(2, out _);
                Enqueue(Message.MouseButton(2, false, nx, ny));
                return true;
            case HookMouseKind.MiddleDown:
                _pressedButtons.TryAdd(3, 0);
                Enqueue(Message.MouseButton(3, true, nx, ny));
                return true;
            case HookMouseKind.MiddleUp:
                _pressedButtons.TryRemove(3, out _);
                Enqueue(Message.MouseButton(3, false, nx, ny));
                return true;
            case HookMouseKind.XDown:
                _pressedButtons.TryAdd(3 + e.Data, 0);
                Enqueue(Message.MouseButton(3 + e.Data, true, nx, ny));
                return true;
            case HookMouseKind.XUp:
                _pressedButtons.TryRemove(3 + e.Data, out _);
                Enqueue(Message.MouseButton(3 + e.Data, false, nx, ny));
                return true;
            case HookMouseKind.Wheel:
                Enqueue(Message.Wheel(e.Data, 0, nx, ny));
                return true;
            case HookMouseKind.HWheel:
                Enqueue(Message.Wheel(0, e.Data, nx, ny));
                return true;
            default:
                return false;
        }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static (float Nx, float Ny) Normalize(int x, int y)
    {
        var width = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
        var height = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
        return (Math.Clamp(x / (float)width, 0f, 1f), Math.Clamp(y / (float)height, 0f, 1f));
    }

    private void Enqueue(Message message)
    {
        if (_outbox.Count > 8192)
        {
            _outbox.TryDequeue(out _);
            Log.Warn("发送队列积压，丢弃一条输入事件");
        }

        _outbox.Enqueue(message);
    }

    private async Task ToggleAsync()
    {
        if (!await _transition.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_remote) await ExitRemoteCoreAsync("hotkey").ConfigureAwait(false);
            else await EnterRemoteCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NotifyError("切换失败: " + ex.Message);
        }
        finally
        {
            _transition.Release();
        }
    }

    public Task ToggleAsyncPublic() => ToggleAsync();

    private async Task EnterRemoteCoreAsync()
    {
        SecureChannel? channel;
        lock (_gate) channel = _channel;
        if (channel is null || !_connected)
        {
            NotifyError("Mac 尚未连接，请确认 Mac 端 OneDesk 已启动并配对");
            return;
        }

        try
        {
            await channel.SendAsync(new Message { Type = MessageType.Take }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NotifyError("无法通知 Mac 接管: " + ex.Message);
            return;
        }

        if (GetCursorPos(out var cursor))
        {
            _savedX = cursor.X;
            _savedY = cursor.Y;
        }
        else
        {
            _savedX = int.MinValue;
        }

        _remote = true;
        _pressedKeys.Clear();
        _pressedButtons.Clear();
        Interlocked.Exchange(ref _mouseDirty, 0);
        RaiseState();
        Log.Info("已切换到 Mac 控制");

        if (_config.ClipboardSync)
        {
            _ = Task.Run(SendClipboardToClientAsync);
        }

        if (_config.Monitor.Enabled)
        {
            var (ok, message) = await Task.Run(() => Ddc.SetInput(_config.Monitor.MonitorIndex,
                _config.Monitor.MonitorNameContains, _config.Monitor.PeerInput)).ConfigureAwait(false);
            if (!ok)
            {
                NotifyError("显示器切换失败：" + message);
                await ExitRemoteCoreAsync("ddc-failed").ConfigureAwait(false);
            }
            else
            {
                Log.Info("显示器 -> Mac: " + message);
            }
        }
    }

    private async Task ExitRemoteCoreAsync(string reason)
    {
        if (!_remote) return;

        _remote = false;
        ReleasePressedToClient();
        Enqueue(new Message { Type = MessageType.Release });
        RestoreCursor();
        RaiseState();
        Log.Info("已切回 Windows 本地控制 (" + reason + ")");

        // Give the queued key-ups + Release a chance to leave before the session might be closed.
        try
        {
            await Task.Delay(80).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        if (_config.Monitor.Enabled)
        {
            // This usually fails because this machine's DDC bus is not the active input.
            // The Mac performs the authoritative switch in its Release handler; this is just a fallback.
            _ = Task.Run(async () =>
            {
                await Task.Delay(600).ConfigureAwait(false);
                var (ok, message) = Ddc.SetInput(_config.Monitor.MonitorIndex,
                    _config.Monitor.MonitorNameContains, _config.Monitor.MyInput);
                if (!ok)
                {
                    Log.Info("本机（非活动输入）切回失败，等待 Mac 端切回: " + message);
                }
            });
        }
    }

    private void ReleasePressedToClient()
    {
        foreach (var (vk, info) in _pressedKeys)
        {
            Enqueue(Message.Key(vk, info.Scan, info.Extended ? KeyFlags.Extended : 0));
        }

        foreach (var button in _pressedButtons.Keys)
        {
            Enqueue(Message.MouseButton(button, false, _mouseNx, _mouseNy));
        }

        _pressedKeys.Clear();
        _pressedButtons.Clear();
    }

    private void ClearPressed()
    {
        _pressedKeys.Clear();
        _pressedButtons.Clear();
        Interlocked.Exchange(ref _mouseDirty, 0);
    }

    private void RestoreCursor()
    {
        if (_savedX == int.MinValue) return;
        try
        {
            SetCursorPos(_savedX, _savedY);
        }
        catch
        {
            // ignored
        }
    }

    private async Task SendClipboardToClientAsync()
    {
        if (!_config.ClipboardSync) return;
        var text = WinClipboard.GetText();
        if (string.IsNullOrEmpty(text)) return;

        SecureChannel? channel;
        lock (_gate) channel = _channel;
        if (channel is null) return;

        try
        {
            await channel.SendAsync(Message.Clipboard(text), CancellationToken.None).ConfigureAwait(false);
            Log.Info($"已发送剪贴板到 Mac（{text.Length} 字符）");
        }
        catch (Exception ex)
        {
            Log.Warn("发送剪贴板失败: " + ex.Message);
        }
    }

    public async Task SendClipboardNowAsync()
    {
        if (!_connected)
        {
            NotifyError("Mac 尚未连接");
            return;
        }

        await SendClipboardToClientAsync().ConfigureAwait(false);
    }

    public async Task<string> TestMonitorAsync()
    {
        var cfg = _config;
        var (ok1, msg1) = await Task.Run(() => Ddc.SetInput(cfg.Monitor.MonitorIndex,
            cfg.Monitor.MonitorNameContains, cfg.Monitor.PeerInput)).ConfigureAwait(false);
        if (!ok1) return "切换到对端失败：" + msg1;

        await Task.Delay(3000).ConfigureAwait(false);
        var (ok2, msg2) = await Task.Run(() => Ddc.SetInput(cfg.Monitor.MonitorIndex,
            cfg.Monitor.MonitorNameContains, cfg.Monitor.MyInput)).ConfigureAwait(false);
        return $"切到对端成功；3 秒后切回{(ok2 ? "成功" : "失败")}：{msg2}";
    }

    public async Task<string> ReadMonitorInputAsync()
    {
        var cfg = _config;
        var (value, message) = await Task.Run(() => Ddc.GetInput(cfg.Monitor.MonitorIndex,
            cfg.Monitor.MonitorNameContains)).ConfigureAwait(false);
        return value is null ? message : message;
    }

    public async Task<bool> ApplyConfigAsync(AppConfig newConfig)
    {
        if (!await _transition.WaitAsync(3000).ConfigureAwait(false))
        {
            NotifyError("正在切换中，请稍后重试");
            return false;
        }

        var previous = _config;
        try
        {
            newConfig.Normalize();

            // Persist first so a validation/disk failure leaves the running service untouched.
            newConfig.Save();
            if (_remote) await ExitRemoteCoreAsync("config").ConfigureAwait(false);
            await StopListenerAsync().ConfigureAwait(false);

            _config = newConfig.Clone();
            _lastError = "";
            if (StartListener()) return true;

            // The new port could not be opened. Roll back to the previous configuration
            // instead of leaving the host with no listener at all.
            _config = previous;
            try
            {
                previous.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("恢复旧配置失败: " + ex.Message);
            }

            StartListener();
            return false;
        }
        catch (Exception ex)
        {
            NotifyError("应用配置失败: " + ex.Message);
            return false;
        }
        finally
        {
            _transition.Release();
            RaiseState();
        }
    }

    private void CloseSession()
    {
        SecureChannel? channel;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            channel = _channel;
            cts = _sessionCts;
            _channel = null;
            _sessionCts = null;
            _connected = false;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignored
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

        if (_remote)
        {
            _remote = false;
            ClearPressed();
            RestoreCursor();
            RaiseState();
        }
    }

    private void NotifyError(string message)
    {
        _lastError = message;
        Log.Warn(message);
        try
        {
            Notification?.Invoke(message, true);
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

        _listenerCts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // ignored
        }

        CloseSession();
        _hook.Dispose();
        _transition.Dispose();
        _connectionGate.Dispose();
    }
}
