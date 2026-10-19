using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDesk.Core;

/// <summary>
/// Minimal loopback-only HTTP server used by the macOS agent for configuration.
/// Deliberately has no external dependencies and no listening on non-loopback interfaces.
/// </summary>
public sealed class LocalWebServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly Func<object> _status;
    private readonly Func<AppConfig> _getConfig;
    private readonly Func<AppConfig, string?> _saveConfig;
    private readonly Func<string, string> _action;
    private readonly Func<string> _logTail;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _loop;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public LocalWebServer(int port, Func<object> status, Func<AppConfig> getConfig,
        Func<AppConfig, string?> saveConfig, Func<string, string> action, Func<string> logTail)
    {
        _port = port;
        _status = status;
        _getConfig = getConfig;
        _saveConfig = saveConfig;
        _action = action;
        _logTail = logTail;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public int Port => _port;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log.Info($"Web 配置界面: http://127.0.0.1:{_port}/");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("Web 接受连接失败: " + ex.Message);
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken outerCt)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote is not null && !IPAddress.IsLoopback(remote.Address)) return;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var req = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (req is null) return;

                var (method, path, query, headers, body) = req.Value;
                var authOk = headers.TryGetValue("x-onedesk-token", out var token) && token == _token;

                if (path == "/" || path == "/index.html")
                {
                    await WriteAsync(stream, 200, "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes(BuildHtml()), timeout.Token).ConfigureAwait(false);
                    return;
                }

                if (path == "/favicon.ico")
                {
                    await WriteAsync(stream, 204, "text/plain", Array.Empty<byte>(), timeout.Token).ConfigureAwait(false);
                    return;
                }

                if (!authOk)
                {
                    await WriteAsync(stream, 403, "application/json",
                        Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"forbidden\"}"), timeout.Token).ConfigureAwait(false);
                    return;
                }

                switch (path)
                {
                    case "/api/status":
                        await WriteJsonAsync(stream, _status(), timeout.Token).ConfigureAwait(false);
                        return;

                    case "/api/config" when method == "GET":
                        await WriteJsonAsync(stream, _getConfig(), timeout.Token).ConfigureAwait(false);
                        return;

                    case "/api/config" when method == "POST":
                    {
                        var cfg = JsonSerializer.Deserialize<AppConfig>(body, JsonOptions);
                        if (cfg is null)
                        {
                            await WriteJsonAsync(stream, new { ok = false, error = "无效的配置" }, timeout.Token).ConfigureAwait(false);
                            return;
                        }

                        var err = _saveConfig(cfg);
                        await WriteJsonAsync(stream, err is null
                            ? new { ok = true }
                            : new { ok = false, error = err }, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    case "/api/action" when method == "POST":
                    {
                        var name = "";
                        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var kv = part.Split('=', 2);
                            if (kv.Length == 2 && kv[0] == "name") name = Uri.UnescapeDataString(kv[1]);
                        }

                        if (name.Length == 0 && body.Length > 0)
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(body);
                                if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                            }
                            catch
                            {
                                // ignored
                            }
                        }

                        var result = _action(name);
                        await WriteJsonAsync(stream, new { ok = true, message = result }, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    case "/api/log":
                        await WriteAsync(stream, 200, "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes(_logTail()), timeout.Token).ConfigureAwait(false);
                        return;

                    default:
                        await WriteJsonAsync(stream, new { ok = false, error = "not found" }, timeout.Token, 404)
                            .ConfigureAwait(false);
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                // timeout
            }
            catch (Exception ex)
            {
                Log.Warn("Web 请求失败: " + ex.Message);
            }
        }
    }

    private static async Task<(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Body)?>
        ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;

        while (buffer.Length < 64 * 1024)
        {
            var n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (n == 0) return null;
            buffer.Write(chunk, 0, n);
            var data = buffer.GetBuffer();
            headerEnd = FindHeaderEnd(data, (int)buffer.Length);
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return null;

        var all = buffer.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return null;
        var method = requestLine[0].ToUpperInvariant();
        var rawPath = requestLine[1];
        var queryIndex = rawPath.IndexOf('?');
        var path = queryIndex >= 0 ? rawPath[..queryIndex] : rawPath;
        var query = queryIndex >= 0 ? rawPath[(queryIndex + 1)..] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx <= 0) continue;
            headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }

        var bodyStart = headerEnd + 4;
        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lenText)) int.TryParse(lenText, out contentLength);
        if (contentLength < 0 || contentLength > 1024 * 1024) return null;

        var body = new byte[contentLength];
        var already = Math.Max(0, all.Length - bodyStart);
        if (already > 0) Buffer.BlockCopy(all, bodyStart, body, 0, Math.Min(already, contentLength));
        var remaining = contentLength - Math.Min(already, contentLength);
        if (remaining > 0)
        {
            await stream.ReadExactlyAsync(body.AsMemory(contentLength - remaining, remaining), ct).ConfigureAwait(false);
        }

        return (method, path, query, headers, body);
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) return i;
        }

        return -1;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, object value, CancellationToken ct, int status = 200)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await WriteAsync(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), ct)
            .ConfigureAwait(false);
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body,
        CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK"
        };

        var headers = $"HTTP/1.1 {status} {reason}\r\n" +
                      $"Content-Type: {contentType}\r\n" +
                      $"Content-Length: {body.Length}\r\n" +
                      "Cache-Control: no-store\r\n" +
                      "X-Frame-Options: DENY\r\n" +
                      "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'\r\n" +
                      "Connection: close\r\n\r\n";
        var head = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private string BuildHtml()
    {
        var html = HtmlTemplate.Replace("__TOKEN__", _token)
            .Replace("__PORT__", _port.ToString());
        return html;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // ignored
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        _cts.Dispose();
    }

    private const string HtmlTemplate = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>OneDesk</title>
<style>
:root { color-scheme: dark; --bg:#0f1115; --card:#181c24; --line:#2a3140; --text:#e6eaf2; --muted:#93a0b5; --accent:#5b8cff; --ok:#43d17c; --warn:#ffb454; --bad:#ff6b6b; }
* { box-sizing: border-box; }
body { margin:0; background:radial-gradient(1200px 600px at 20% -10%, #1b2740 0%, var(--bg) 55%); color:var(--text); font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,"PingFang SC","Microsoft YaHei",sans-serif; }
.wrap { max-width:820px; margin:0 auto; padding:36px 22px 80px; }
header { display:flex; align-items:center; gap:12px; margin-bottom:26px; }
.logo { width:40px; height:40px; border-radius:12px; background:linear-gradient(135deg,#5b8cff,#7f5bff); box-shadow:0 8px 30px #5b8cff44; }
h1 { font-size:22px; margin:0; letter-spacing:.3px; }
.sub { color:var(--muted); font-size:12px; }
.card { background:linear-gradient(180deg,#1a1f29,#161a22); border:1px solid var(--line); border-radius:16px; padding:20px; margin-bottom:18px; box-shadow:0 20px 60px #0006; }
.card h2 { font-size:14px; margin:0 0 14px; color:#cbd5e7; font-weight:600; letter-spacing:.4px; text-transform:uppercase; }
.status { display:grid; grid-template-columns:repeat(auto-fit,minmax(170px,1fr)); gap:12px; }
.stat { background:#11151c; border:1px solid #232a36; border-radius:12px; padding:12px 14px; }
.stat .k { color:var(--muted); font-size:11px; text-transform:uppercase; letter-spacing:.6px; }
.stat .v { margin-top:4px; font-size:14px; font-weight:600; }
.badge { display:inline-flex; align-items:center; gap:7px; }
.dot { width:8px; height:8px; border-radius:50%; background:var(--muted); box-shadow:0 0 0 4px #ffffff08; }
.dot.ok { background:var(--ok); box-shadow:0 0 0 4px #43d17c22,0 0 14px #43d17c88; }
.dot.bad { background:var(--bad); box-shadow:0 0 0 4px #ff6b6b22; }
.dot.warn { background:var(--warn); box-shadow:0 0 0 4px #ffb45422; }
label { display:block; color:var(--muted); font-size:12px; margin:0 0 6px; }
.grid { display:grid; grid-template-columns:1fr 1fr; gap:14px; }
@media (max-width:620px) { .grid { grid-template-columns:1fr; } }
input, select { width:100%; background:#0e1218; color:var(--text); border:1px solid #2c3545; border-radius:10px; padding:10px 12px; font:inherit; outline:none; }
input:focus, select:focus { border-color:#5b8cff88; box-shadow:0 0 0 3px #5b8cff22; }
.row { display:flex; gap:10px; flex-wrap:wrap; margin-top:16px; }
button { background:#232c3d; color:var(--text); border:1px solid #344057; border-radius:10px; padding:10px 16px; font:inherit; cursor:pointer; transition:.15s; }
button:hover { background:#2b3750; border-color:#46587a; }
button.primary { background:linear-gradient(135deg,#5b8cff,#6f6bff); border:0; font-weight:600; }
button.primary:hover { filter:brightness(1.08); }
button.ghost { background:transparent; }
.hint { color:var(--muted); font-size:12px; margin-top:10px; }
pre { background:#0c0f14; border:1px solid #232a36; border-radius:12px; padding:14px; overflow:auto; max-height:280px; color:#9fb0c8; font-size:12px; }
.msg { margin-top:14px; font-size:13px; min-height:20px; }
.msg.ok { color:var(--ok); } .msg.err { color:var(--bad); }
.chk { display:flex; align-items:center; gap:8px; color:#c9d3e4; font-size:13px; margin-top:12px; }
.chk input { width:auto; }
.foot { color:#5c687c; font-size:11px; margin-top:22px; text-align:center; }
a { color:#7ea2ff; }
</style>
</head>
<body>
<div class="wrap">
  <header>
    <div class="logo"></div>
    <div><h1>OneDesk</h1><div class="sub">Mac 端控制面板 · <span id="hostname"></span></div></div>
  </header>

  <div class="card">
    <h2>运行状态</h2>
    <div class="status">
      <div class="stat"><div class="k">连接</div><div class="v"><span id="conn" class="badge"><span class="dot"></span><span>未知</span></span></div></div>
      <div class="stat"><div class="k">控制权</div><div class="v"><span id="control" class="badge"><span class="dot"></span><span>未知</span></span></div></div>
      <div class="stat"><div class="k">辅助功能权限</div><div class="v"><span id="perm" class="badge"><span class="dot"></span><span>未知</span></span></div></div>
      <div class="stat"><div class="k">显示器</div><div class="v" id="display">—</div></div>
    </div>
    <div class="hint" id="lastError"></div>
  </div>

  <div class="card">
    <h2>连接设置</h2>
    <form id="form">
      <div class="grid">
        <div><label>Windows 主机地址</label><input id="peerHost" placeholder="例如 192.168.1.20" autocomplete="off"></div>
        <div><label>端口</label><input id="port" type="number" min="1024" max="65535"></div>
        <div><label>配对密码</label><input id="password" placeholder="与 Windows 端一致" autocomplete="off"></div>
        <div><label>本机接口（Mac）</label><select id="myInput"></select></div>
        <div><label>对端接口（Windows）</label><select id="peerInput"></select></div>
        <div><label>m1ddc 显示器</label><input id="macDisplay" placeholder="留空自动选择外接屏" autocomplete="off"></div>
      </div>
      <div class="chk"><input type="checkbox" id="clipboard"><label for="clipboard" style="margin:0">切换时同步剪贴板（纯文本）</label></div>
      <div class="chk"><input type="checkbox" id="swap"><label for="swap" style="margin:0">把 Ctrl 映射为 Command，Win 映射为 Control</label></div>
      <div class="chk"><input type="checkbox" id="alternate"><label for="alternate" style="margin:0">使用 m1ddc 备用输入值（部分 LG 显示器）</label></div>
      <div class="chk"><input type="checkbox" id="autoConnect"><label for="autoConnect" style="margin:0">启动后自动连接</label></div>
      <div class="row">
        <button class="primary" type="submit">保存并应用</button>
        <button type="button" class="ghost" onclick="act('reconnect')">重新连接</button>
        <button type="button" class="ghost" onclick="act('test-peer')">测试切到 Windows（3 秒后切回）</button>
        <button type="button" class="ghost" onclick="act('test-my')">测试切回 Mac</button>
        <button type="button" class="ghost" onclick="act('sync-clipboard')">发送 Mac 剪贴板</button>
        <button type="button" class="ghost" onclick="act('open-accessibility')">辅助功能设置</button>
      </div>
      <div class="msg" id="msg"></div>
    </form>
  </div>

  <div class="card">
    <h2>日志</h2>
    <div class="row" style="margin-top:0"><button class="ghost" onclick="loadLog()">刷新日志</button></div>
    <pre id="log">点击“刷新日志”查看。</pre>
    <div class="hint">首次使用需要在“系统设置 → 隐私与安全性 → 辅助功能”中勾选 OneDesk。切换显示器输入依赖 m1ddc。</div>
  </div>

  <div class="foot">OneDesk 1.0 · 仅监听 127.0.0.1:__PORT__</div>
</div>

<script>
const TOKEN = "__TOKEN__";
const PRESETS = [["DisplayPort 1",15],["DisplayPort 2",16],["HDMI 1",17],["HDMI 2",18],["USB-C / DP Alt",27],["DVI",3],["VGA",1]];
const $ = id => document.getElementById(id);
let cfg = null;

function fillSelect(el, value) {
  el.innerHTML = "";
  const seen = new Set();
  for (const [name, v] of PRESETS) {
    const o = document.createElement("option"); o.value = v; o.textContent = name + " (" + v + ")";
    el.appendChild(o); seen.add(v);
  }
  if (!seen.has(Number(value))) {
    const o = document.createElement("option"); o.value = value; o.textContent = "自定义 (" + value + ")";
    el.appendChild(o);
  }
  el.value = String(value);
}

async function api(path, opts = {}) {
  const r = await fetch(path, { ...opts, headers: { "X-OneDesk-Token": TOKEN, ...(opts.headers || {}) } });
  return await r.json();
}

function badge(el, text, cls) {
  el.innerHTML = '<span class="dot ' + cls + '"></span><span>' + text + '</span>';
}

async function load() {
  const s = await api("/api/status");
  $("hostname").textContent = s.hostname || "";
  badge($("conn"), s.connected ? "已连接" : (s.autoConnect ? "等待连接" : "未启用"), s.connected ? "ok" : "warn");
  badge($("control"), s.controlled ? "Mac 正在被控制" : "Windows 本地控制", s.controlled ? "ok" : "");
  badge($("perm"), s.accessibility ? "已授权" : "未授权", s.accessibility ? "ok" : "bad");
  $("display").textContent = s.display || "—";
  $("lastError").textContent = s.lastError ? "最近错误：" + s.lastError : "";
}

async function loadConfig() {
  cfg = await api("/api/config");
  $("peerHost").value = cfg.peerHost || "";
  $("port").value = cfg.port;
  $("password").value = cfg.password || "";
  $("macDisplay").value = cfg.monitor.macDisplay || "";
  $("clipboard").checked = !!cfg.clipboardSync;
  $("swap").checked = !!cfg.swapCtrlCmd;
  $("alternate").checked = !!cfg.monitor.macAlternateInput;
  $("autoConnect").checked = !!cfg.autoConnect;
  fillSelect($("myInput"), cfg.monitor.myInput);
  fillSelect($("peerInput"), cfg.monitor.peerInput);
}

function msg(text, ok) {
  const m = $("msg"); m.textContent = text; m.className = "msg " + (ok ? "ok" : "err");
}

$("form").addEventListener("submit", async e => {
  e.preventDefault();
  cfg.peerHost = $("peerHost").value.trim();
  cfg.port = Number($("port").value);
  cfg.password = $("password").value.trim();
  cfg.clipboardSync = $("clipboard").checked;
  cfg.swapCtrlCmd = $("swap").checked;
  cfg.autoConnect = $("autoConnect").checked;
  cfg.monitor.myInput = Number($("myInput").value);
  cfg.monitor.peerInput = Number($("peerInput").value);
  cfg.monitor.macDisplay = $("macDisplay").value.trim();
  cfg.monitor.macAlternateInput = $("alternate").checked;
  const r = await api("/api/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(cfg) });
  msg(r.ok ? "已保存，正在应用…" : ("保存失败：" + r.error), r.ok);
  if (r.ok) { await new Promise(r => setTimeout(r, 700)); await load(); }
});

async function act(name) {
  const r = await api("/api/action?name=" + encodeURIComponent(name), { method: "POST" });
  msg(r.message || r.error || "完成", r.ok);
  setTimeout(load, 800);
}

async function loadLog() {
  const r = await fetch("/api/log", { headers: { "X-OneDesk-Token": TOKEN } });
  $("log").textContent = await r.text();
}

load(); loadConfig();
setInterval(load, 2000);
setInterval(loadLog, 5000);
</script>
</body>
</html>
""";
}
