using System.Diagnostics;
using System.Text;
using OneDesk.Core;

namespace OneDesk.Client;

public static class MacClipboard
{
    private const int MaxUtf8Bytes = 1_500_000;

    private static string Limit(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxUtf8Bytes) return text;

        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, mid)) <= MaxUtf8Bytes) lo = mid;
            else hi = mid - 1;
        }

        if (lo > 0 && char.IsHighSurrogate(text[lo - 1])) lo--;
        return text[..lo];
    }

    public static string GetText()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/pbpaste",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return "";
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // ignored
                }

                return "";
            }

            var text = outputTask.GetAwaiter().GetResult();
            return Limit(text);
        }
        catch (Exception ex)
        {
            Log.Warn("读取 Mac 剪贴板失败: " + ex.Message);
            return "";
        }
    }

    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        text = Limit(text);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/pbcopy",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // ignored
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("写入 Mac 剪贴板失败: " + ex.Message);
            return false;
        }
    }
}
