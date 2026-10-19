using Microsoft.Win32;
using OneDesk.Core;

namespace OneDesk.Client.Win;

/// <summary>
/// Autostart for the Windows client. It uses its own Run value so a machine that runs both the
/// host and the client does not have one overwrite the other.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OneDeskClient";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("更新开机启动失败: " + ex.Message);
        }
    }
}
