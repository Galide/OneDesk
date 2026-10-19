using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OneDesk.Core;

var failures = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : ": " + detail)}");
    }
}

Console.WriteLine("OneDesk self-test");
Console.WriteLine();

// Config
var cfg = new AppConfig { Password = "  ABCD-1234-EF56-7890 ", PeerHost = " 192.168.1.10 " };
cfg.Normalize();
Check("password canonicalization", cfg.Password == "abcd1234ef567890", cfg.Password);
Check("peer host trimming", cfg.PeerHost == "192.168.1.10");
Check("grouped password", cfg.GroupedPassword() == "abcd-1234-ef56-7890", cfg.GroupedPassword());
var clone = cfg.Clone();
clone.Monitor.PeerInput = 18;
Check("config clone is deep", cfg.Monitor.PeerInput != 18);
Check("hotkey description", new HotkeySpec { Ctrl = true, Alt = true, VirtualKey = 0x20 }.Describe() == "Ctrl+Alt+Space");

// Key mapping
Check("A maps to macOS keycode 0", KeyMap.ToMacKeyCode(0x41, 0, false, false) == 0);
Check("B maps to macOS keycode 11", KeyMap.ToMacKeyCode(0x42, 0, false, false) == 11);
Check("unsupported key returns null", KeyMap.ToMacKeyCode(0x13, 0, false, false) is null);
Check("Cmd keycode 55", KeyMap.ToMacKeyCode(0x5B, 0, true, false) == 55);
Check("Ctrl maps to Control keycode", KeyMap.ToMacKeyCode(0x11, 0, false, false) == 59);
Check("swap maps Ctrl to Command", KeyMap.ToMacKeyCode(0x11, 0, false, true) == 55);
Check("modifier flag", KeyMap.ModifierFlag(0xA3, false) == MacModifiers.Control);
Check("swap modifier flag", KeyMap.ModifierFlag(0x5B, true) == MacModifiers.Control);

// Protocol round trips
var messages = new[]
{
    Message.Hello("mac-mini"),
    new Message { Type = MessageType.HelloAck, Version = 1, Name = "windows" },
    new Message { Type = MessageType.Take },
    new Message { Type = MessageType.Release },
    new Message { Type = MessageType.ReleaseAck },
    new Message { Type = MessageType.Ping },
    new Message { Type = MessageType.Pong },
    Message.Key(0x41, 0x1E, KeyFlags.Down | KeyFlags.Extended | KeyFlags.Repeat),
    Message.Key(0x41, 0x1E, KeyFlags.None),
    Message.MouseMove(0.25f, 0.75f),
    Message.MouseButton(1, true, 0.1f, 0.2f),
    Message.Wheel(-120, 120, 0.5f, 0.5f),
    Message.Clipboard("hello 世界")
};

foreach (var message in messages)
{
    var decoded = Protocol.Decode(Protocol.Encode(message));
    var same = decoded.Type == message.Type &&
               decoded.Vk == message.Vk &&
               decoded.Scan == message.Scan &&
               decoded.Flags == message.Flags &&
               Math.Abs(decoded.Nx - message.Nx) < 0.0001 &&
               Math.Abs(decoded.Ny - message.Ny) < 0.0001 &&
               decoded.Button == message.Button &&
               decoded.Down == message.Down &&
               decoded.Delta == message.Delta &&
               decoded.HDelta == message.HDelta &&
               decoded.Text == message.Text &&
               decoded.Name == message.Name;
    Check("roundtrip " + message.Type, same);
}

// Secure channel over loopback
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
const string password = "unit-test-password";

var serverTask = Task.Run(async () =>
{
    using var serverClient = await listener.AcceptTcpClientAsync();
    serverClient.NoDelay = true;
    var stream = serverClient.GetStream();
    var salt = await SecureChannel.ReadSaltAsync(stream, CancellationToken.None);
    var key = SecureChannel.DeriveKey(password, salt);
    using var channel = new SecureChannel(stream, key);
    var hello = await channel.ReceiveAsync(CancellationToken.None);
    if (hello?.Type != MessageType.Hello || hello.Name != "mac-mini") throw new Exception("bad hello");
    await channel.SendAsync(new Message { Type = MessageType.HelloAck, Name = "windows" }, CancellationToken.None);
    var keyMessage = await channel.ReceiveAsync(CancellationToken.None);
    if (keyMessage?.Type != MessageType.Key || keyMessage.Vk != 0x41) throw new Exception("bad key");
    await channel.SendAsync(Message.Clipboard(new string('x', 200_000)), CancellationToken.None);
    await channel.SendAsync(new Message { Type = MessageType.Ping }, CancellationToken.None);
});

try
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    client.NoDelay = true;
    var stream = client.GetStream();
    var salt = SecureChannel.NewSalt();
    await SecureChannel.WriteSaltAsync(stream, salt, CancellationToken.None);
    var key = SecureChannel.DeriveKey(password, salt);
    using var channel = new SecureChannel(stream, key);
    await channel.SendAsync(Message.Hello("mac-mini"), CancellationToken.None);
    var ack = await channel.ReceiveAsync(CancellationToken.None);
    Check("secure handshake", ack?.Type == MessageType.HelloAck && ack.Name == "windows");
    await channel.SendAsync(Message.Key(0x41, 0x1E, KeyFlags.Down), CancellationToken.None);
    var clipboard = await channel.ReceiveAsync(CancellationToken.None);
    Check("secure large clipboard", clipboard?.Type == MessageType.Clipboard && clipboard.Text?.Length == 200_000);
    var ping = await channel.ReceiveAsync(CancellationToken.None);
    Check("secure ping", ping?.Type == MessageType.Ping);
    await serverTask;
}
catch (Exception ex)
{
    Check("secure channel", false, ex.Message);
}
finally
{
    listener.Stop();
}

// Wrong password must fail
var listener2 = new TcpListener(IPAddress.Loopback, 0);
listener2.Start();
var port2 = ((IPEndPoint)listener2.LocalEndpoint).Port;
var serverTask2 = Task.Run(async () =>
{
    using var serverClient = await listener2.AcceptTcpClientAsync();
    var stream = serverClient.GetStream();
    var salt = await SecureChannel.ReadSaltAsync(stream, CancellationToken.None);
    using var channel = new SecureChannel(stream, SecureChannel.DeriveKey(password, salt));
    try
    {
        await channel.ReceiveAsync(CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        return true;
    }

    return false;
});

try
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port2);
    var stream = client.GetStream();
    var salt = SecureChannel.NewSalt();
    await SecureChannel.WriteSaltAsync(stream, salt, CancellationToken.None);
    using var channel = new SecureChannel(stream, SecureChannel.DeriveKey("wrong-password", salt));
    await channel.SendAsync(Message.Hello("intruder"), CancellationToken.None);
    Check("wrong password rejected", await serverTask2);
}
catch (Exception ex)
{
    Check("wrong password rejected", false, ex.Message);
}
finally
{
    listener2.Stop();
}

// Local web API contract (the browser UI expects camelCase JSON)
var webConfig = new AppConfig { Password = "web-test-password", PeerHost = "10.0.0.5" };
webConfig.Normalize();
var webApplied = false;
var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var webPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();

try
{
    await using var web = new LocalWebServer(
        webPort,
        () => new { hostname = "test-host", connected = true },
        () => webConfig,
        cfg =>
        {
            webApplied = cfg.PeerHost == "10.0.0.7" && cfg.Monitor.MyInput == 16;
            return null;
        },
        _ => "ok",
        () => "log");

    web.Start();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}/") };
    var html = await http.GetStringAsync("");
    var tokenMatch = System.Text.RegularExpressions.Regex.Match(html, "const TOKEN = \"([0-9a-f]+)\";");
    Check("web UI serves token", tokenMatch.Success);

    using var getRequest = new HttpRequestMessage(HttpMethod.Get, "api/config");
    getRequest.Headers.Add("X-OneDesk-Token", tokenMatch.Groups[1].Value);
    var configJson = await (await http.SendAsync(getRequest)).Content.ReadAsStringAsync();
    using var configDoc = JsonDocument.Parse(configJson);
    var configRoot = configDoc.RootElement;
    Check("web config uses camelCase",
        configRoot.TryGetProperty("peerHost", out var peer) && peer.GetString() == "10.0.0.5" &&
        !configRoot.TryGetProperty("PeerHost", out _));
    Check("web nested config uses camelCase",
        configRoot.TryGetProperty("monitor", out var monitor) &&
        monitor.TryGetProperty("myInput", out _) &&
        !monitor.TryGetProperty("MyInput", out _));

    using var postRequest = new HttpRequestMessage(HttpMethod.Post, "api/config")
    {
        Content = new StringContent("{\"peerHost\":\"10.0.0.7\",\"monitor\":{\"myInput\":16}}",
            Encoding.UTF8, "application/json")
    };
    postRequest.Headers.Add("X-OneDesk-Token", tokenMatch.Groups[1].Value);
    var postResponse = await http.SendAsync(postRequest);
    Check("web config POST accepted", postResponse.IsSuccessStatusCode && webApplied);

    using var unauthorized = new HttpRequestMessage(HttpMethod.Get, "api/config");
    var unauthorizedResponse = await http.SendAsync(unauthorized);
    Check("web API rejects missing token", (int)unauthorizedResponse.StatusCode == 403);
}
catch (Exception ex)
{
    Check("local web API", false, ex.Message);
}

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("All tests passed.");
    return 0;
}

Console.WriteLine($"{failures} test(s) failed.");
return 1;
