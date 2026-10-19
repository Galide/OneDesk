using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using OneDesk.Core;

namespace OneDesk.Client;

internal static class Program
{
    private const string AgentLabel = "com.onedesk.client";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // no console / redirected output
        }

        Log.Init(Path.Combine(AppConfig.LogDirectory, "client.log"));

        try
        {
            if (args.Contains("--help") || args.Contains("-h"))
            {
                PrintHelp();
                return 0;
            }

            if (args.Contains("--setup")) return Setup();
            if (args.Contains("--install-agent")) return InstallAgent();
            if (args.Contains("--uninstall-agent")) return UninstallAgent();
            if (args.Contains("--list-displays")) return ListDisplays();
            if (args.Contains("--status")) return Status();
            var testDdc = Array.IndexOf(args, "--test-ddc");
            if (testDdc >= 0) return TestDdc(args, testDdc);

            return await RunDaemonAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("未处理异常", ex);
            Console.Error.WriteLine("OneDesk 错误: " + ex.Message);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
OneDesk macOS 端

  --daemon            后台运行（launchd 使用）
  --setup             终端向导，配置角色、密码和显示器输入
  --install-agent     安装并启动 ~/Library/LaunchAgents 登录代理
  --uninstall-agent   停止并删除登录代理
  --list-displays     列出 CoreGraphics 显示器和 m1ddc 显示器
  --test-ddc <值>     直接测试 DDC 输入切换（15=DP1, 17=HDMI1, 18=HDMI2）
  --status            显示配置和显示器信息

同一份程序既可以是主机（键盘鼠标插在 Mac 上）也可以是客户端（受 Windows 控制），
在配置面板里随时切换角色。

配置面板： http://127.0.0.1:47822/
日志：     ~/Library/Logs/OneDesk/client.log
""");
    }

    private static async Task<int> RunDaemonAsync()
    {
        Directory.CreateDirectory(AppConfig.DataDirectory);
        FileStream? lockFile = null;
        try
        {
            lockFile = new FileStream(Path.Combine(AppConfig.DataDirectory, "agent.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Log.Warn("OneDesk 代理已经在运行");
            return 2;
        }

        await using var _ = lockFile;
        var config = AppConfig.Load();
        var agent = new MacAgent(config);
        LocalWebServer? web = null;

        try
        {
            web = new LocalWebServer(
                config.WebPort,
                agent.BuildStatus,
                () => agent.Config,
                cfg => agent.Save(cfg),
                name => agent.RunAction(name),
                () => Log.Tail(500));
            web.Start();
        }
        catch (Exception ex)
        {
            Log.Error("启动配置网页失败（端口被占用？）: " + ex.Message);
        }

        agent.Start();
        Log.Info($"OneDesk 已启动（{(config.Role == AppRole.Host ? "主机" : "客户端")}），" +
                 $"配置: {AppConfig.ConfigPath}");
        if (config.Role == AppRole.Client && string.IsNullOrWhiteSpace(config.PeerHost))
        {
            Log.Warn("尚未配置 Windows 地址，请打开 http://127.0.0.1:" + config.WebPort + "/ 进行设置");
        }

        var exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exit.TrySetResult(true);
        };

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            exit.TrySetResult(true);
        });
        using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
        {
            ctx.Cancel = true;
            exit.TrySetResult(true);
        });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            exit.TrySetResult(true);
        });

        await exit.Task.ConfigureAwait(false);
        Log.Info("正在退出…");
        await agent.DisposeAsync().ConfigureAwait(false);
        if (web is not null) await web.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static int Setup()
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("--setup 需要在交互式终端中运行。");
            return 1;
        }

        var config = AppConfig.Load();

        Console.WriteLine("OneDesk macOS 配置向导");
        Console.WriteLine("========================");
        var role = Prompt("键盘鼠标插在这台 Mac 上吗？(y=主机 / n=客户端)",
            config.Role == AppRole.Host ? "y" : "n").Trim().ToLowerInvariant();
        config.Role = role is "y" or "yes" or "host" ? AppRole.Host : AppRole.Client;

        if (config.Role == AppRole.Client)
        {
            config.PeerHost = Prompt("对端主机 IP（插着键盘鼠标的那台）", config.PeerHost);
        }

        config.Port = PromptInt("端口", config.Port, 1024, 65535);

        var password = Prompt("配对密码（留空则使用当前密码）", config.Password);
        if (!string.IsNullOrWhiteSpace(password)) config.Password = password;
        config.Password = AppConfig.CanonicalizePassword(config.Password);
        if (config.Password.Length < 8) config.Password = AppConfig.GeneratePassword();

        Console.WriteLine();
        Console.WriteLine("接口值: 15=DisplayPort 1, 16=DisplayPort 2, 17=HDMI 1, 18=HDMI 2, 27=USB-C");
        config.Monitor.MyInput = PromptInt("Mac 接在显示器的哪个接口", config.Monitor.MyInput, 0, 255);
        config.Monitor.PeerInput = PromptInt("Windows 接在显示器的哪个接口", config.Monitor.PeerInput, 0, 255);

        var displays = new MacDdc().GetDisplayList();
        if (!string.IsNullOrWhiteSpace(displays))
        {
            Console.WriteLine();
            Console.WriteLine("m1ddc 显示器列表:");
            Console.WriteLine(displays);
        }

        config.Monitor.MacDisplay = Prompt("m1ddc 显示器 UUID（留空自动选择外接屏）", config.Monitor.MacDisplay);
        config.ClipboardSync = PromptBool("切换时同步剪贴板", config.ClipboardSync);
        config.SwapCtrlCmd = PromptBool("把 Ctrl 映射为 Command", config.SwapCtrlCmd);
        config.InvertScroll = PromptBool("反转鼠标滚轮方向", config.InvertScroll);
        config.AutoConnect = true;

        config.Save();
        Console.WriteLine();
        Console.WriteLine("配置已保存: " + AppConfig.ConfigPath);
        Console.WriteLine("本机角色: " + (config.Role == AppRole.Host ? "主机（键盘鼠标在本机）" : "客户端（受另一台控制）"));
        Console.WriteLine("配对密码: " + config.GroupedPassword());

        if (config.Role == AppRole.Host)
        {
            foreach (var address in AppConfig.LocalIPv4Addresses())
            {
                Console.WriteLine("在另一台电脑上填写主机地址: " + address);
            }
        }

        if (PromptBool("安装并启动登录代理", true))
        {
            InstallAgent();
        }

        Console.WriteLine("打开配置面板: http://127.0.0.1:" + config.WebPort + "/");
        return 0;
    }

    private static string Prompt(string label, string current)
    {
        Console.Write($"{label}" + (string.IsNullOrEmpty(current) ? "" : $" [{current}]") + ": ");
        var value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    private static int PromptInt(string label, int current, int min, int max)
    {
        while (true)
        {
            var text = Prompt(label, current.ToString());
            if (int.TryParse(text, out var value) && value >= min && value <= max) return value;
            Console.WriteLine($"请输入 {min}-{max} 之间的数字。");
        }
    }

    private static bool PromptBool(string label, bool current)
    {
        var text = Prompt(label + " (y/n)", current ? "y" : "n").ToLowerInvariant();
        return text is "y" or "yes" or "1" or "true";
    }

    private static int InstallAgent()
    {
        var exe = ResolveExecutable();
        if (exe is null)
        {
            Console.Error.WriteLine("找不到 OneDesk 可执行文件。");
            return 1;
        }

        if (exe.Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("OneDesk 正在从 Gatekeeper 临时位置运行。");
            Console.Error.WriteLine("请先把 OneDesk.app 拖到“应用程序”文件夹，再从那里运行 --install-agent。");
            return 1;
        }

        if (!exe.StartsWith("/Applications/", StringComparison.Ordinal))
        {
            Console.WriteLine("提示：OneDesk.app 不在 /Applications 中；登录代理会引用当前路径。");
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        Directory.CreateDirectory(dir);
        var plistPath = Path.Combine(dir, AgentLabel + ".plist");
        var escaped = SecurityElement.Escape(exe)!;
        var plist = $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>{AgentLabel}</string>
  <key>ProgramArguments</key>
  <array><string>{escaped}</string><string>--daemon</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ProcessType</key><string>Interactive</string>
  <key>StandardOutPath</key><string>/tmp/onedesk-launchd.log</string>
  <key>StandardErrorPath</key><string>/tmp/onedesk-launchd.log</string>
</dict>
</plist>
""";
        File.WriteAllText(plistPath, plist);

        var uid = GetUid();
        Run("/bin/launchctl", "bootout", $"gui/{uid}/{AgentLabel}");
        var boot = Run("/bin/launchctl", "bootstrap", $"gui/{uid}", plistPath);
        Run("/bin/launchctl", "enable", $"gui/{uid}/{AgentLabel}");
        Run("/bin/launchctl", "kickstart", "-k", $"gui/{uid}/{AgentLabel}");
        Console.WriteLine(boot.Code == 0
            ? "登录代理已安装并启动。"
            : $"登录代理写入完成，但启动返回 {boot.Code}: {boot.Output}");
        Console.WriteLine("可执行文件: " + exe);
        return 0;
    }

    private static int UninstallAgent()
    {
        var plistPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", AgentLabel + ".plist");
        var uid = GetUid();
        Run("/bin/launchctl", "bootout", $"gui/{uid}/{AgentLabel}");
        try
        {
            if (File.Exists(plistPath)) File.Delete(plistPath);
            Console.WriteLine("登录代理已删除。");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("删除失败: " + ex.Message);
            return 1;
        }

        return 0;
    }

    private static int ListDisplays()
    {
        Console.WriteLine("CoreGraphics 显示器:");
        foreach (var display in MacDisplay.GetActive())
        {
            Console.WriteLine("  " + display);
        }

        Console.WriteLine();
        Console.WriteLine("m1ddc display list:");
        Console.WriteLine(new MacDdc().GetDisplayList());
        return 0;
    }

    private static int TestDdc(string[] args, int index)
    {
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value))
        {
            Console.WriteLine("用法: --test-ddc <值>");
            return 1;
        }

        var config = AppConfig.Load();
        var ddc = new MacDdc();
        var (ok, message) = ddc.SetInput(value, config.Monitor.MacAlternateInput, config.Monitor.MacDisplay);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    private static int Status()
    {
        var config = AppConfig.Load();
        var ddc = new MacDdc();
        Console.WriteLine("配置文件: " + AppConfig.ConfigPath);
        Console.WriteLine("本机角色: " + (config.Role == AppRole.Host ? "主机（键盘鼠标在本机）" : "客户端（受另一台控制）"));
        if (config.Role == AppRole.Client)
        {
            Console.WriteLine("对端主机: " + (string.IsNullOrWhiteSpace(config.PeerHost) ? "未配置" : config.PeerHost));
        }
        else
        {
            var addresses = AppConfig.LocalIPv4Addresses();
            Console.WriteLine("本机地址: " + (addresses.Count > 0 ? string.Join(", ", addresses) : "未检测到"));
        }

        Console.WriteLine("端口:     " + config.Port);
        Console.WriteLine("配对密码: " + config.GroupedPassword());
        Console.WriteLine("热键:     " + config.Hotkey.Describe());
        Console.WriteLine("Mac 接口: " + config.Monitor.MyInput);
        Console.WriteLine("对端接口: " + config.Monitor.PeerInput);
        Console.WriteLine("m1ddc:    " + (ddc.Available ? ddc.Path : "未找到"));
        Console.WriteLine("辅助功能: " + (MacPermissions.HasAccess ? "已授权" : "未授权"));
        Console.WriteLine();
        Console.WriteLine("m1ddc 显示器:");
        Console.WriteLine(ddc.GetDisplayList());
        return 0;
    }

    private static string? ResolveExecutable()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && !Path.GetFileName(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(path);
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "OneDesk");
        return File.Exists(candidate) ? candidate : null;
    }

    private static int GetUid()
    {
        var result = Run("/usr/bin/id", "-u");
        return int.TryParse(result.Output.Trim(), out var uid) ? uid : 501;
    }

    private static (int Code, string Output) Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null) return (-1, "无法启动 " + file);
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return (process.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
