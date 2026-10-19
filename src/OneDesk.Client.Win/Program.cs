using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using OneDesk.Core;
using OneDesk.Platform.Win;

namespace OneDesk.Client.Win;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--list-monitors") || args.Contains("--get-input") || args.Contains("--set-input") ||
            args.Contains("--status") || args.Contains("--help") || args.Contains("-h"))
        {
            RunConsoleCommand(args);
            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Init();

        using var mutex = new Mutex(true, @"Local\OneDesk.Client", out var isNew);
        if (!isNew)
        {
            if (!args.Contains("--minimized"))
            {
                MessageBox.Show("OneDesk 客户端已经在运行（请查看系统托盘）。", "OneDesk 客户端",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return;
        }

        var firstRun = !File.Exists(AppConfig.ConfigPath);
        var config = AppConfig.Load();
        if (config.Role != AppRole.Client)
        {
            config.Role = AppRole.Client;
            config.Save();
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("界面线程异常", e.Exception);
            MessageBox.Show("OneDesk 客户端遇到错误：" + e.Exception.Message + "\n\n详情见日志。", "OneDesk 客户端",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("未处理异常", ex);
        };

        if (firstRun) Startup.SetEnabled(config.StartAutomatically);

        // Also reopen the settings window when a config exists but the host was never filled in
        // (for example when --status created the file before the first GUI start).
        var needsSetup = firstRun || string.IsNullOrWhiteSpace(config.PeerHost);

        using var service = new ClientService(config);
        service.Start();
        using var tray = new TrayApp(service, needsSetup);
        Application.Run(tray);
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
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

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
OneDesk Windows 客户端

  --status            显示配置、显示器和管理员权限状态
  --list-monitors     列出物理显示器和当前输入值
  --get-input         读取当前 VCP 0x60 值
  --set-input <值>    直接切换显示器输入源
  --minimized         启动时不打开设置窗口（开机启动使用）

配置： %APPDATA%\OneDesk\config.json
日志： %APPDATA%\OneDesk\logs\onedesk.log
""");
            return;
        }

        var config = AppConfig.Load();

        if (args.Contains("--status"))
        {
            Console.WriteLine("配置文件:   " + AppConfig.ConfigPath);
            Console.WriteLine("主机地址:   " + (string.IsNullOrWhiteSpace(config.PeerHost) ? "未配置" : config.PeerHost));
            Console.WriteLine("端口:       " + config.Port);
            Console.WriteLine("配对密码:   " + config.GroupedPassword());
            Console.WriteLine("本机接口:   " + config.Monitor.MyInput + " (" + Ddc.InputName(config.Monitor.MyInput) + ")");
            Console.WriteLine("主机接口:   " + config.Monitor.PeerInput + " (" + Ddc.InputName(config.Monitor.PeerInput) + ")");
            Console.WriteLine("自动切换:   " + (config.Monitor.Enabled ? "开" : "关"));
            Console.WriteLine("剪贴板同步: " + (config.ClipboardSync ? "开" : "关"));
            Console.WriteLine("开机启动:   " + (Startup.IsEnabled ? "开" : "关"));
            Console.WriteLine("管理员权限: " + (IsElevated() ? "是" : "否（无法向以管理员运行的窗口注入输入）"));
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
            Console.WriteLine("用法: OneDeskClient.exe --set-input <VCP值>   例如 --set-input 17");
        }
    }
}
