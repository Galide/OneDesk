using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

// Windows client input mapping (pure helpers, no P/Invoke)
Check("absolute origin", WinInputMap.ToAbsolute(0d) == 0);
Check("absolute far edge", WinInputMap.ToAbsolute(1d) == 65535);
Check("absolute midpoint rounds up", WinInputMap.ToAbsolute(0.5d) == 32768);
Check("absolute quarter", WinInputMap.ToAbsolute(0.25d) == 16384);
Check("absolute clamps below", WinInputMap.ToAbsolute(-3d) == 0);
Check("absolute clamps above", WinInputMap.ToAbsolute(4d) == 65535);
Check("absolute rejects NaN", WinInputMap.ToAbsolute(double.NaN) == 0);
Check("left button flags",
    WinInputMap.ButtonFlag(1, true) == WinInputMap.LeftDown &&
    WinInputMap.ButtonFlag(1, false) == WinInputMap.LeftUp);
Check("right button flags",
    WinInputMap.ButtonFlag(2, true) == WinInputMap.RightDown &&
    WinInputMap.ButtonFlag(2, false) == WinInputMap.RightUp);
Check("middle button flags",
    WinInputMap.ButtonFlag(3, true) == WinInputMap.MiddleDown &&
    WinInputMap.ButtonFlag(3, false) == WinInputMap.MiddleUp);
Check("x button flags",
    WinInputMap.ButtonFlag(4, true) == WinInputMap.XDown &&
    WinInputMap.ButtonFlag(5, false) == WinInputMap.XUp);
Check("unknown button rejected", WinInputMap.ButtonFlag(9, true) == 0);
Check("x button data", WinInputMap.XButtonData(4) == 1 && WinInputMap.XButtonData(5) == 2);
Check("wheel axis flags",
    WinInputMap.WheelFlag(false) == WinInputMap.Wheel && WinInputMap.WheelFlag(true) == WinInputMap.HorizontalWheel);
Check("scancode key down keeps extended prefix",
    WinInputMap.KeyEventFlags(KeyFlags.Down | KeyFlags.Extended, true) ==
    (WinInputMap.ScanCode | WinInputMap.ExtendedKey));
Check("virtual-key event drops the extended prefix",
    WinInputMap.KeyEventFlags(KeyFlags.Down | KeyFlags.Extended, false) == 0);
Check("key release sets KEYEVENTF_KEYUP",
    WinInputMap.KeyEventFlags(KeyFlags.None, true) == (WinInputMap.ScanCode | WinInputMap.KeyUp));
Check("repeat is just another key down",
    WinInputMap.KeyEventFlags(KeyFlags.Down | KeyFlags.Repeat, true) == WinInputMap.ScanCode);

// macOS host key mapping
Check("mac A maps to Windows VK/scan",
    MacKeyMap.TryMap(0, out var macA, out var macAScan, out var macAExt) &&
    macA == 0x41 && macAScan == 0x1E && !macAExt);
Check("mac 55 is left command",
    MacKeyMap.TryMap(55, out var cmd, out var cmdScan, out var cmdExt) &&
    cmd == 0x5B && cmdScan == 0x5B && cmdExt);
Check("mac 54 is right command",
    MacKeyMap.TryMap(54, out var rcmd, out _, out _) && rcmd == 0x5C);
Check("mac 59 is left control, mac 62 right control",
    MacKeyMap.TryMap(59, out var lctl, out var lctlScan, out var lctlExt) && lctl == 0xA2 && lctlScan == 0x1D && !lctlExt &&
    MacKeyMap.TryMap(62, out var rctl, out var rctlScan, out var rctlExt) && rctl == 0xA3 && rctlScan == 0x1D && rctlExt);
Check("mac 58 is left option, mac 61 right option",
    MacKeyMap.TryMap(58, out var lalt, out var laltScan, out var laltExt) && lalt == 0xA4 && laltScan == 0x38 && !laltExt &&
    MacKeyMap.TryMap(61, out var ralt, out var raltScan, out var raltExt) && ralt == 0xA5 && raltScan == 0x38 && raltExt);
Check("mac 123 is left arrow (extended)",
    MacKeyMap.TryMap(123, out var left, out var leftScan, out var leftExt) &&
    left == 0x25 && leftScan == 0x4B && leftExt);
Check("mac 126 is up arrow (extended)",
    MacKeyMap.TryMap(126, out var up, out _, out var upExt) && up == 0x26 && upExt);
Check("mac 117 is forward delete (extended)",
    MacKeyMap.TryMap(117, out var fdel, out var fdelScan, out var fdelExt) &&
    fdel == 0x2E && fdelScan == 0x53 && fdelExt);
Check("mac 76 is keypad enter (extended enter)",
    MacKeyMap.TryMap(76, out var nent, out var nentScan, out var nentExt) &&
    nent == 0x0D && nentScan == 0x1C && nentExt);
Check("mac F1..F12 map to VK_F1..F12",
    MacKeyMap.TryMap(122, out var f1, out _, out _) && f1 == 0x70 &&
    MacKeyMap.TryMap(120, out var f2, out _, out _) && f2 == 0x71 &&
    MacKeyMap.TryMap(111, out var f12, out _, out _) && f12 == 0x7B);
Check("mac modifiers are recognised as FlagsChanged keys",
    MacKeyMap.IsModifier(55) && MacKeyMap.IsModifier(62) && !MacKeyMap.IsModifier(0));
Check("unknown mac key is rejected", !MacKeyMap.TryMap(63, out _, out _, out _)); // Fn
Check("hotkey matches mac key with modifiers",
    MacKeyMap.MatchesHotkey(new HotkeySpec { Ctrl = true, Alt = true, VirtualKey = 0x20 },
        49, ctrl: true, alt: true, shift: false, command: false) &&
    !MacKeyMap.MatchesHotkey(new HotkeySpec { Ctrl = true, Alt = true, VirtualKey = 0x20 },
        49, ctrl: true, alt: false, shift: false, command: false));

// Every key the Windows client can be told about must have a scan code for the macOS host,
// and every key the macOS host produces must be reachable in the shared table.
var missingScan = KeyMap.SimplePairs.Select(pair => pair.Key)
    .Where(vk => !ScanMap.TryGet(vk, out var scan, out _) || scan == 0)
    .ToList();
Check("every client mappable VK has a scan code", missingScan.Count == 0, string.Join(",", missingScan));

var unmapped = new List<string>();
foreach (var (macKey, vk) in MacKeyMap.Pairs)
{
    if (!MacKeyMap.TryMap(macKey, out var mapped, out var scan, out _) || mapped != vk || scan == 0)
    {
        unmapped.Add($"0x{macKey:X2}");
    }
}
Check("every macOS key maps to a scan code", unmapped.Count == 0, string.Join(",", unmapped));

if (OperatingSystem.IsWindows())
{
    // The authoritative check: the scan codes the Windows client replays with SendInput.
    var mismatches = new List<string>();
    foreach (var (vk, expected) in ScanMap.All)
    {
        var packed = TestNative.MapVirtualKey((uint)vk, 4 /* MAPVK_VK_TO_VSC_EX */);
        var scan = (int)(packed & 0xFF);
        var winExtended = (packed >> 8) == 0xE0;
        // MapVirtualKey only sets the E0 prefix for keys that exist *solely* in the extended
        // set (right Ctrl/Alt, Windows, divide). For the navigation keys it returns the base
        // code even though SendInput needs the prefix, so the flag is only checked one way.
        if (scan != expected.Scan || (winExtended && !expected.Extended))
        {
            mismatches.Add($"0x{vk:X2}: table=0x{expected.Scan:X2}{(expected.Extended ? "+E0" : "")} " +
                           $"win=0x{scan:X2}{(winExtended ? "+E0" : "")}");
        }
    }

    Check("scan codes agree with MapVirtualKey", mismatches.Count == 0, string.Join(" | ", mismatches));
}

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
var webRole = AppRole.Client;
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
            webRole = cfg.Role;
            return null;
        },
        _ => "ok",
        () => "log");

    web.Start();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}/") };
    var html = await http.GetStringAsync("");
    var tokenMatch = System.Text.RegularExpressions.Regex.Match(html, "const TOKEN = \"([0-9a-f]+)\";");
    Check("web UI serves token", tokenMatch.Success);
    Check("web UI offers the role switch",
        html.Contains("id=\"role\"") && html.Contains("value=\"Host\"") &&
        html.Contains("id=\"toggleBtn\"") && html.Contains("id=\"roleName\""));

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
        Content = new StringContent(
            "{\"role\":\"Host\",\"peerHost\":\"10.0.0.7\",\"monitor\":{\"myInput\":16}}",
            Encoding.UTF8, "application/json")
    };
    postRequest.Headers.Add("X-OneDesk-Token", tokenMatch.Groups[1].Value);
    var postResponse = await http.SendAsync(postRequest);
    Check("web config POST accepted", postResponse.IsSuccessStatusCode && webApplied);
    Check("web config POST carries the role", webRole == AppRole.Host);

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

internal static class TestNative
{
    /// <summary>MAPVK_VK_TO_VSC_EX: low byte = scan code, 0xE0 in the high byte = extended.</summary>
    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint code, uint mapType);
}
