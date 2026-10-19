using System.Runtime.InteropServices;
using OneDesk.Core;
using OneDesk.Platform.Win;

namespace OneDesk.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const string MutexName = @"Local\OneDesk";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a is "--list-monitors" or "--get-input" or "--set-input" or "--status"
                or "--help" or "/?" or "-h"))
        {
            RunConsoleCommand(args);
            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Init();

        using var mutex = new Mutex(true, MutexName, out var isNew);
        if (!isNew)
        {
            if (!args.Contains("--minimized"))
            {
                MessageBox.Show("OneDesk 已经在运行（请查看系统托盘）。", "OneDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return;
        }

        var firstRun = !File.Exists(AppConfig.ConfigPath);
        var config = AppConfig.Load();
        if (firstRun)
        {
            try
            {
                config.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("保存初始配置失败: " + ex.Message);
            }
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("界面线程异常", e.Exception);
            MessageBox.Show("OneDesk 遇到错误：" + e.Exception.Message + "\n\n详情见日志。", "OneDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("未处理异常", ex);
        };

        if (firstRun) Startup.SetEnabled(config.StartAutomatically);

        using var app = new AppHost(config);
        app.Start();
        using var tray = new TrayApp(app, firstRun);
        Application.Run(tray);
    }

    private static void RunConsoleCommand(string[] args)
    {
        try
        {
            AttachConsole(-1);
            var stdout = Console.OpenStandardOutput();
            Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
        }
        catch
        {
            // no parent console
        }

        var config = AppConfig.Load();

        if (args.Contains("--help") || args.Contains("/?") || args.Contains("-h"))
        {
            Console.WriteLine("OneDesk — 一套键盘鼠标，控制两台电脑");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  OneDesk.exe                     启动托盘程序（主机 / 客户端由设置决定）");
            Console.WriteLine("  OneDesk.exe --minimized         静默启动（登录时使用）");
            Console.WriteLine("  OneDesk.exe --status            显示当前角色、密码和显示器配置");
            Console.WriteLine("  OneDesk.exe --list-monitors     列出支持 DDC/CI 的显示器");
            Console.WriteLine("  OneDesk.exe --get-input         读取显示器当前输入源");
            Console.WriteLine("  OneDesk.exe --set-input <值>    切换显示器输入源，例如 --set-input 17");
            return;
        }

        if (args.Contains("--status"))
        {
            Console.WriteLine($"角色: {(config.Role == AppRole.Host ? "主机" : "客户端")}");
            Console.WriteLine($"配置文件: {AppConfig.ConfigPath}");
            Console.WriteLine(File.Exists(AppConfig.ConfigPath)
                ? $"配对密码: {config.GroupedPassword()}"
                : "配对密码: （首次启动 OneDesk 时生成）");
            Console.WriteLine($"端口: {config.Port}");
            if (config.Role == AppRole.Client) Console.WriteLine($"主机地址: {config.PeerHost}");
            else Console.WriteLine($"本机地址: {string.Join(", ", AppConfig.LocalIPv4Addresses())}");
            Console.WriteLine($"本机接口: {Ddc.InputName(config.Monitor.MyInput)} ({config.Monitor.MyInput})");
            Console.WriteLine($"对端接口: {Ddc.InputName(config.Monitor.PeerInput)} ({config.Monitor.PeerInput})");
            Console.WriteLine($"自动切换显示器: {(config.Monitor.Enabled ? "开" : "关")}");
            Console.WriteLine($"同步剪贴板: {(config.ClipboardSync ? "开" : "关")}");
            Console.WriteLine($"开机启动: {(Startup.IsEnabled ? "开" : "关")}");
            return;
        }

        if (args.Contains("--list-monitors"))
        {
            var monitors = Ddc.ListMonitors(true);
            Console.WriteLine($"检测到 {monitors.Count} 个物理显示器:");
            foreach (var monitor in monitors) Console.WriteLine("  " + monitor.Label);
            return;
        }

        if (args.Contains("--get-input"))
        {
            var (_, message) = Ddc.GetInput(config.Monitor.MonitorIndex, config.Monitor.MonitorNameContains);
            Console.WriteLine(message);
            return;
        }

        var index = Array.IndexOf(args, "--set-input");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var input))
        {
            var (ok, message) = Ddc.SetInput(config.Monitor.MonitorIndex, config.Monitor.MonitorNameContains, input);
            Console.WriteLine((ok ? "OK: " : "FAIL: ") + message);
        }
        else
        {
            Console.WriteLine("用法: OneDesk.exe --set-input <VCP值>   例如 --set-input 17");
        }
    }
}
