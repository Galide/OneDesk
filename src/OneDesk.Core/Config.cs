using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDesk.Core;

public enum AppRole
{
    Host,
    Client
}

public sealed class HotkeySpec
{
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>Windows virtual-key code. 0x20 is Space.</summary>
    public int VirtualKey { get; set; } = 0x20;

    public bool Matches(bool ctrl, bool alt, bool shift, bool win, int vk)
    {
        return vk == VirtualKey && Ctrl == ctrl && Alt == alt && Shift == shift && Win == win;
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static string KeyName(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);

        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B => "LeftWin",
            0x5C => "RightWin",
            0x5D => "Menu",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => "VK" + vk.ToString("X2")
        };
    }
}

public sealed class MonitorConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>VCP 0x60 value of the input this machine is connected to.</summary>
    public int MyInput { get; set; } = 15;

    /// <summary>VCP 0x60 value of the input the other machine is connected to.</summary>
    public int PeerInput { get; set; } = 17;

    /// <summary>Flattened physical-monitor index on Windows.</summary>
    public int MonitorIndex { get; set; }

    /// <summary>Optional substring match for a Windows monitor description.</summary>
    public string MonitorNameContains { get; set; } = "";

    /// <summary>How long the monitor needs to settle before it is worth sending keys again.</summary>
    public int SettleMs { get; set; } = 800;

    /// <summary>m1ddc display UUID or index on macOS. Empty means auto-detect the external display.</summary>
    public string MacDisplay { get; set; } = "";

    /// <summary>Use m1ddc's alternate input addressing (some LG displays).</summary>
    public bool MacAlternateInput { get; set; }
}

public sealed class AppConfig
{
    public int ConfigVersion { get; set; } = 1;
    public AppRole Role { get; set; } = AppRole.Host;

    /// <summary>Address of the Windows host. Only used by the client.</summary>
    public string PeerHost { get; set; } = "";
    public int Port { get; set; } = 47821;

    /// <summary>Shared secret. Dashes and whitespace are ignored.</summary>
    public string Password { get; set; } = "";

    public HotkeySpec Hotkey { get; set; } = new();
    public MonitorConfig Monitor { get; set; } = new();
    public bool ClipboardSync { get; set; } = true;

    /// <summary>Map the physical Ctrl key to Command and the Win key to Control.</summary>
    public bool SwapCtrlCmd { get; set; }

    /// <summary>Invert vertical scrolling on the Mac.</summary>
    public bool InvertScroll { get; set; }

    public bool StartAutomatically { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public int WebPort { get; set; } = 47822;
    public string ClientName { get; set; } = Environment.MachineName;
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int HeartbeatTimeoutMs { get; set; } = 8000;

    public static readonly (string Name, int Value)[] InputPresets =
    {
        ("DisplayPort 1", 15),
        ("DisplayPort 2", 16),
        ("HDMI 1", 17),
        ("HDMI 2", 18),
        ("USB-C / DP Alt", 27),
        ("DVI", 3),
        ("VGA", 1)
    };

    [JsonIgnore]
    public string CanonicalPassword => CanonicalizePassword(Password);

    public static string CanonicalizePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "";
        var chars = password.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToLowerInvariant);
        return new string(chars.ToArray());
    }

    public AppConfig Clone()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.Hotkey = new HotkeySpec
        {
            Ctrl = Hotkey.Ctrl,
            Alt = Hotkey.Alt,
            Shift = Hotkey.Shift,
            Win = Hotkey.Win,
            VirtualKey = Hotkey.VirtualKey
        };
        copy.Monitor = new MonitorConfig
        {
            Enabled = Monitor.Enabled,
            MyInput = Monitor.MyInput,
            PeerInput = Monitor.PeerInput,
            MonitorIndex = Monitor.MonitorIndex,
            MonitorNameContains = Monitor.MonitorNameContains,
            SettleMs = Monitor.SettleMs,
            MacDisplay = Monitor.MacDisplay,
            MacAlternateInput = Monitor.MacAlternateInput
        };
        copy.InvertScroll = InvertScroll;
        return copy;
    }

    public void Normalize()
    {
        PeerHost = (PeerHost ?? "").Trim();
        Password = CanonicalizePassword(Password);
        if (Password.Length == 0) Password = GeneratePassword();
        if (Password.Length < 8) Password = Password.PadRight(8, '0');

        Port = Math.Clamp(Port, 1024, 65535);
        WebPort = Math.Clamp(WebPort, 1024, 65535);
        if (WebPort == Port) WebPort = Port == 47822 ? 47823 : 47822;
        ConnectTimeoutMs = Math.Clamp(ConnectTimeoutMs, 1000, 30000);
        HeartbeatTimeoutMs = Math.Clamp(HeartbeatTimeoutMs, 3000, 60000);
        Monitor.MyInput = Math.Clamp(Monitor.MyInput, 0, 255);
        Monitor.PeerInput = Math.Clamp(Monitor.PeerInput, 0, 255);
        Monitor.MonitorIndex = Math.Max(0, Monitor.MonitorIndex);
        Monitor.SettleMs = Math.Clamp(Monitor.SettleMs, 0, 5000);
        Hotkey ??= new HotkeySpec();
        if (Hotkey.VirtualKey == 0 || HotkeySpec.IsModifier(Hotkey.VirtualKey)) Hotkey.VirtualKey = 0x20;
        if (!Hotkey.Ctrl && !Hotkey.Alt && !Hotkey.Shift && !Hotkey.Win) Hotkey.Ctrl = true;
        ClientName = string.IsNullOrWhiteSpace(ClientName) ? Environment.MachineName : ClientName.Trim();
    }

    public static string GeneratePassword()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    }

    public string GroupedPassword()
    {
        var p = CanonicalPassword;
        if (p.Length != 16) return p;
        return string.Join("-", Enumerable.Range(0, 4).Select(i => p.Substring(i * 4, 4)));
    }

    public static string DataDirectory
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "OneDesk");
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneDesk");
        }
    }

    public static string LogDirectory
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Logs", "OneDesk");
            }

            return Path.Combine(DataDirectory, "logs");
        }
    }

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                cfg.Normalize();
                return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取配置失败，将创建新配置: " + ex.Message);
            try
            {
                if (File.Exists(ConfigPath))
                {
                    File.Copy(ConfigPath, ConfigPath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
            }
            catch
            {
                // ignored
            }
        }

        var fresh = new AppConfig();
        fresh.Normalize();
        try
        {
            fresh.Save();
        }
        catch (Exception ex)
        {
            Log.Error("写入默认配置失败: " + ex.Message);
        }

        return fresh;
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, true);
    }

    public static IReadOnlyList<string> LocalIPv4Addresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        result.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        return result;
    }
}
