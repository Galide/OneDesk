using System.Runtime.InteropServices;
using System.Windows.Forms;
using OneDesk.Core;

namespace OneDesk.Host;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--list-monitors") || args.Contains("--get-input") || args.Contains("--set-input"))
        {
            RunConsoleCommand(args);
            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Init();

        using var mutex = new Mutex(true, @"Local\OneDesk.Host", out var isNew);
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
        if (config.Role != AppRole.Host)
        {
            config.Role = AppRole.Host;
            config.Save();
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

        using var service = new HostService(config);
        service.Start();
        using var tray = new TrayApp(service, firstRun);
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
        if (args.Contains("--list-monitors"))
        {
            var monitors = Ddc.ListMonitors(true);
            Console.WriteLine($"检测到 {monitors.Count} 个物理显示器:");
            foreach (var monitor in monitors) Console.WriteLine("  " + monitor.Label);
            return;
        }

        if (args.Contains("--get-input"))
        {
            var (value, message) = Ddc.GetInput(config.Monitor.MonitorIndex, config.Monitor.MonitorNameContains);
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
