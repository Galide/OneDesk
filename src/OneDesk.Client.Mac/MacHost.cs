using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OneDesk.Core;
using static OneDesk.Client.MacNative;
using Message = OneDesk.Core.Message;

namespace OneDesk.Client;

/// <summary>
/// macOS side of a pair where the keyboard and mouse are plugged into the Mac. It listens for
/// the other machine, captures local input with a CGEventTap and forwards it over the encrypted
/// session; the monitor is switched with m1ddc.
/// </summary>
public sealed class MacHost : IDisposable
{
    private readonly object _gate = new();
    private readonly MacCapture _capture = new();
    private readonly MacDdc _ddc = new();
    private readonly ConcurrentQueue<Message> _outbox = new();
    private readonly ConcurrentDictionary<int, (int Scan, bool Extended)> _pressedKeys = new();
    private readonly ConcurrentDictionary<int, byte> _pressedButtons = new();
    private readonly HashSet<ushort> _unsupportedKeys = new();
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
    private float _mouseNx;
    private float _mouseNy;
    private int _mouseDirty;
    private string _lastError = "";
    private string _lastDdc = "";
    private bool _disposed;

    public event Action? StateChanged;

    public MacHost(AppConfig config)
    {
        _config = config.Clone();
        _config.Normalize();
        _capture.KeyHandler = OnKey;
        _capture.MouseHandler = OnMouse;
    }

    public AppConfig Config => _config;
    public bool IsRemote => _remote;
    public bool IsConnected => _connected;
    public bool CaptureRunning => _capture.IsRunning;
    public bool IsListening => _listener is not null;
    public string LastError => _lastError;
    public string DdcStatus => _lastDdc;

    public void Start()
    {
        MacPermissions.Request();
        if (!_capture.Start())
        {
            _lastError = "无法监听键盘/鼠标，请授予辅助功能权限";
            Log.Error(_lastError);
        }

        StartListener();
        Log.Info($"macOS Host 启动，热键 {_config.Hotkey.Describe()}");
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
            Log.Info($"监听 0.0.0.0:{_config.Port}，本机地址: " +
                     string.Join(", ", AppConfig.LocalIPv4Addresses()));
            return true;
        }
        catch (Exception ex)
        {
            _lastError = "监听端口失败: " + ex.Message;
            Log.Error(_lastError);
            return false;
        }
    }

    private void StopListener()
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
                accept.Wait(TimeSpan.FromSeconds(2));
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
                Log.Warn("拒绝额外连接：已有对端");
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

            Log.Info($"对端已连接: {hello.Name} ({client.Client.RemoteEndPoint})");
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
                await Task.WhenAll(receive, send, heartbeat).WaitAsync(TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
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
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    var (ok, msg) = _ddc.SetInput(_config.Monitor.MyInput,
                        _config.Monitor.MacAlternateInput, _config.Monitor.MacDisplay);
                    if (!ok) Log.Warn("断线后尝试切回本机输入失败（对端也会尝试）: " + msg);
                });
            }

            if (gotGate) _connectionGate.Release();
            sessionCts?.Dispose();

            if (current)
            {
                Log.Info(wasRemote ? "对端连接断开，已回到本地控制" : "对端连接已断开");
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
                case MessageType.ReleaseAck:
                    break;
                case MessageType.Clipboard:
                    if (!_remote && _config.ClipboardSync && !string.IsNullOrEmpty(message.Text))
                    {
                        var text = message.Text;
                        _ = Task.Run(() => MacClipboard.SetText(text));
                        Log.Info($"已从对端接收剪贴板（{text.Length} 字符）");
                    }

                    break;
                case MessageType.Error:
                    Log.Warn("对端返回错误: " + message.Text);
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
                Log.Warn($"对端心跳超时（{idle} ms），断开连接");
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

    private bool OnKey(MacKeyEvent e)
    {
        var cfg = _config;
        var ctrl = (e.Flags & FlagControl) != 0;
        var alt = (e.Flags & FlagAlternate) != 0;
        var shift = (e.Flags & FlagShift) != 0;
        var command = (e.Flags & FlagCommand) != 0;

        // The hotkey uses the same Windows-shaped specification as on Windows; Command plays
        // the part of the Windows key.
        if (MacKeyMap.TryGetVk(e.Key, out var pressed) && pressed == cfg.Hotkey.VirtualKey)
        {
            var isHotkey = MacKeyMap.MatchesHotkey(cfg.Hotkey, e.Key, ctrl, alt, shift, command);
            if (!e.Down)
            {
                // Clear the latch even when a modifier is released before the main key.
                _hotkeyLatch = false;
            }
            else if (isHotkey && !_hotkeyLatch)
            {
                _hotkeyLatch = true;
                _ = Task.Run(ToggleCoreAsync);
            }

            if (isHotkey) return true;
        }

        if (!_remote) return false;

        // Caps lock only produces FlagsChanged events; Windows expects a press and a release.
        if (e.Modifier && e.Key == 57)
        {
            Enqueue(Message.Key(0x14, 0x3A, KeyFlags.Down));
            Enqueue(Message.Key(0x14, 0x3A, KeyFlags.None));
            return true;
        }

        if (!MacKeyMap.TryMap(e.Key, out var vk, out var scan, out var extended))
        {
            if (_unsupportedKeys.Add(e.Key))
            {
                Log.Warn($"不支持的 macOS 按键 {e.Key}（已忽略）");
            }

            return true;
        }

        if (e.Down)
        {
            var repeat = e.Repeat || _pressedKeys.ContainsKey(vk);
            if (!repeat) _pressedKeys[vk] = (scan, extended);
            var flags = KeyFlags.Down | (extended ? KeyFlags.Extended : 0) |
                        (repeat ? KeyFlags.Repeat : 0);
            Enqueue(Message.Key(vk, scan, flags));
        }
        else if (_pressedKeys.TryRemove(vk, out var info))
        {
            Enqueue(Message.Key(vk, info.Scan, info.Extended ? KeyFlags.Extended : 0));
        }

        return true;
    }

    private bool OnMouse(MacMouseEvent e)
    {
        if (!_remote) return false;

        switch (e.Kind)
        {
            case MacMouseKind.Move:
                _mouseNx = e.Nx;
                _mouseNy = e.Ny;
                Interlocked.Exchange(ref _mouseDirty, 1);
                return false; // let the local cursor move; the shared screen shows the peer anyway
            case MacMouseKind.ButtonDown:
                _pressedButtons.TryAdd(e.Button, 0);
                Enqueue(Message.MouseButton(e.Button, true, e.Nx, e.Ny));
                return true;
            case MacMouseKind.ButtonUp:
                _pressedButtons.TryRemove(e.Button, out _);
                Enqueue(Message.MouseButton(e.Button, false, e.Nx, e.Ny));
                return true;
            case MacMouseKind.Wheel:
                if (e.Delta != 0) Enqueue(Message.Wheel(e.Delta, 0, e.Nx, e.Ny));
                if (e.HDelta != 0) Enqueue(Message.Wheel(0, e.HDelta, e.Nx, e.Ny));
                return true;
            default:
                return false;
        }
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

    /// <summary>Switches control to the peer, or takes it back if it is already remote.</summary>
    public Task ToggleAsync() => ToggleCoreAsync();

    private async Task ToggleCoreAsync()
    {
        if (!await _transition.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_remote) await ExitRemoteCoreAsync("hotkey").ConfigureAwait(false);
            else await EnterRemoteCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastError = "切换失败: " + ex.Message;
            Log.Warn(_lastError);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task EnterRemoteCoreAsync()
    {
        SecureChannel? channel;
        lock (_gate) channel = _channel;
        if (channel is null || !_connected)
        {
            _lastError = "对端尚未连接，请确认另一台电脑上的 OneDesk 已启动并配对";
            Log.Warn(_lastError);
            return;
        }

        try
        {
            await channel.SendAsync(new Message { Type = MessageType.Take }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastError = "无法通知对端接管: " + ex.Message;
            Log.Warn(_lastError);
            return;
        }

        _remote = true;
        _pressedKeys.Clear();
        _pressedButtons.Clear();
        Interlocked.Exchange(ref _mouseDirty, 0);
        RaiseState();
        Log.Info("已切换到对端控制");

        if (_config.ClipboardSync)
        {
            _ = Task.Run(SendClipboardToPeerAsync);
        }

        if (_config.Monitor.Enabled)
        {
            var (ok, message) = await Task.Run(() => _ddc.SetInput(_config.Monitor.PeerInput,
                _config.Monitor.MacAlternateInput, _config.Monitor.MacDisplay)).ConfigureAwait(false);
            _lastDdc = message;
            if (!ok)
            {
                _lastError = "显示器切换失败：" + message;
                Log.Warn(_lastError);
                await ExitRemoteCoreAsync("ddc-failed").ConfigureAwait(false);
            }
            else
            {
                Log.Info("显示器 -> 对端: " + message);
            }
        }
    }

    private async Task ExitRemoteCoreAsync(string reason)
    {
        if (!_remote) return;

        _remote = false;
        ReleasePressedToPeer();
        Enqueue(new Message { Type = MessageType.Release });
        RaiseState();
        Log.Info("已切回 Mac 本地控制 (" + reason + ")");

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
            // The peer performs the authoritative switch in its Release handler; this is a fallback.
            _ = Task.Run(async () =>
            {
                await Task.Delay(600).ConfigureAwait(false);
                var (ok, message) = _ddc.SetInput(_config.Monitor.MyInput,
                    _config.Monitor.MacAlternateInput, _config.Monitor.MacDisplay);
                _lastDdc = message;
                if (!ok) Log.Info("本机（非活动输入）切回失败，等待对端切回: " + message);
            });
        }
    }

    private void ReleasePressedToPeer()
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

    private async Task SendClipboardToPeerAsync()
    {
        if (!_config.ClipboardSync) return;
        var text = MacClipboard.GetText();
        if (string.IsNullOrEmpty(text)) return;

        SecureChannel? channel;
        lock (_gate) channel = _channel;
        if (channel is null) return;

        try
        {
            await channel.SendAsync(Message.Clipboard(text), CancellationToken.None).ConfigureAwait(false);
            Log.Info($"已发送剪贴板到对端（{text.Length} 字符）");
        }
        catch (Exception ex)
        {
            Log.Warn("发送剪贴板失败: " + ex.Message);
        }
    }

    public async Task<string> SendClipboardNowAsync()
    {
        if (!_connected) return "对端尚未连接";
        if (!_config.ClipboardSync) return "剪贴板同步已在设置中关闭";
        var text = MacClipboard.GetText();
        if (string.IsNullOrEmpty(text)) return "Mac 剪贴板为空";
        await SendClipboardToPeerAsync().ConfigureAwait(false);
        return $"已发送 {text.Length} 个字符到对端";
    }

    /// <summary>Switches the monitor input once; used by the "test" buttons in the web panel.</summary>
    public async Task<string> TestSwitchAsync(int input)
    {
        var cfg = _config;
        var (ok, message) = await Task.Run(() => _ddc.SetInput(input,
            cfg.Monitor.MacAlternateInput, cfg.Monitor.MacDisplay)).ConfigureAwait(false);
        _lastDdc = message;
        if (!ok) _lastError = message;
        RaiseState();
        return message;
    }

    /// <summary>Drops the current session; the peer reconnects on its own.</summary>
    public void RestartSession()
    {
        CloseSession();
        RaiseState();
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
            RaiseState();
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

        StopListener();
        _capture.Dispose();
        _transition.Dispose();
        _connectionGate.Dispose();
    }
}
