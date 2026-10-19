using System.Text;

namespace OneDesk.Core;

/// <summary>Very small thread-safe logger with a rolling in-memory tail for the UI.</summary>
public static class Log
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new();
    private static StreamWriter? _writer;
    private static string? _path;

    public static int RingCapacity { get; set; } = 800;
    public static string? FilePath => _path;

    public static event Action<string>? Written;

    public static void Init(string? path = null)
    {
        lock (Gate)
        {
            try
            {
                _writer?.Dispose();
                _writer = null;
                path ??= Path.Combine(AppConfig.LogDirectory, "onedesk.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxFileBytes)
                    {
                        var backup = path + ".1";
                        File.Delete(backup);
                        File.Move(path, backup);
                    }
                }
                catch
                {
                    // rotation is best effort
                }

                _path = path;
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch
            {
                _writer = null;
            }
        }

        Info($"---- OneDesk {typeof(Log).Assembly.GetName().Version} started ({Environment.MachineName}, " +
             $"{Environment.OSVersion.VersionString}) ----");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", message + " :: " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingCapacity) Ring.Dequeue();
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // logging must never bring the app down
            }
        }

        try
        {
            Written?.Invoke(line);
        }
        catch
        {
            // ignored
        }

        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // no console
        }
    }

    public static string Tail(int lines = 200)
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, Ring.TakeLast(Math.Max(1, lines)));
        }
    }
}
