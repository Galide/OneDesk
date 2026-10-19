using System.Diagnostics;
using System.Text.RegularExpressions;
using OneDesk.Core;

namespace OneDesk.Client;

/// <summary>Wraps the m1ddc command line tool for DDC/CI input switching on Apple Silicon.</summary>
public sealed class MacDdc
{
    private static readonly Regex ListLine = new(@"^\[(\d+)\]\s+(.*?)\s+\(([^)]+)\)\s*$",
        RegexOptions.Compiled);

    private readonly string? _path;

    public MacDdc()
    {
        _path = FindM1Ddc();
        if (_path is null)
        {
            Log.Warn("未找到 m1ddc。显示器自动切回将不可用；请安装：brew install m1ddc，或把 m1ddc 放进 OneDesk.app/Contents/Resources/");
        }
        else
        {
            Log.Info("m1ddc: " + _path);
        }
    }

    public bool Available => _path is not null;
    public string Path => _path ?? "";

    public string GetDisplayList()
    {
        var result = Run(new[] { "display", "list" });
        return result.Ok ? result.Stdout.Trim() : result.Message;
    }

    public (bool Ok, string Message) SetInput(int value, bool alternate, string preferredDisplay)
    {
        if (_path is null)
        {
            return (false, "未找到 m1ddc，请先安装：brew install m1ddc");
        }

        // m1ddc's input-alt mode uses a different set of VCP values (used by some LG
        // displays). Translate the standard values so the UI can keep one numbering.
        if (alternate)
        {
            value = value switch
            {
                15 => 208, // DisplayPort 1
                16 => 209, // DisplayPort 2
                17 => 144, // HDMI 1
                18 => 145, // HDMI 2
                27 => 210, // USB-C / DP 3
                _ => value
            };
        }

        var display = ResolveDisplay(preferredDisplay);
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(display))
        {
            args.Add("display");
            args.Add(display);
        }

        args.Add("set");
        args.Add(alternate ? "input-alt" : "input");
        args.Add(value.ToString());

        var result = Run(args);
        if (result.Ok)
        {
            Log.Info($"m1ddc 切换输入成功: {string.Join(" ", args)}");
            return (true, $"已切换 Mac 显示器输入为 {value}");
        }

        Log.Warn($"m1ddc 切换失败: {string.Join(" ", args)} -> {result.Message}");
        return (false, result.Message);
    }

    private static string NormalizeUuid(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray());

    public string ResolveDisplay(string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred.Trim();

        var text = GetDisplayList();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith('['))
            .ToList();

        var external = MacDisplay.Pick("");
        if (external is not null && !string.IsNullOrEmpty(external.Uuid))
        {
            var wanted = NormalizeUuid(external.Uuid);
            foreach (var line in lines)
            {
                var match = ListLine.Match(line);
                if (match.Success &&
                    NormalizeUuid(match.Groups[3].Value).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups[3].Value.Trim();
                }
            }
        }

        // Fall back to the index of the external display. m1ddc numbers displays in the
        // same order as CGGetActiveDisplayList, which is what MacDisplay uses.
        if (external is not null && external.Index >= 0)
        {
            return (external.Index + 1).ToString();
        }

        return "";
    }

    private (bool Ok, string Stdout, string Message) Run(IReadOnlyList<string> args)
    {
        if (_path is null) return (false, "", "m1ddc 不可用");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return (false, "", "无法启动 m1ddc");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // ignored
                }

                return (false, "", "m1ddc 超时");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode == 0) return (true, stdout, stdout.Trim());

            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (false, stdout, $"m1ddc 退出码 {process.ExitCode}: {message.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, "", "执行 m1ddc 失败: " + ex.Message);
        }
    }

    private static string? FindM1Ddc()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            System.IO.Path.Combine(baseDir, "m1ddc"),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "Resources", "m1ddc")),
            "/opt/homebrew/bin/m1ddc",
            "/usr/local/bin/m1ddc",
            "/usr/bin/m1ddc"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/which",
                ArgumentList = { "m1ddc" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(1000);
            if (!string.IsNullOrWhiteSpace(output) && File.Exists(output)) return output;
        }
        catch
        {
            // ignored
        }

        return null;
    }
}
