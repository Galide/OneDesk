using System.Diagnostics;
using OneDesk.Core;

namespace OneDesk.Client;

public static class MacPermissions
{
    public static bool HasAccess
    {
        get
        {
            try
            {
                return MacNative.CGPreflightPostEventAccess();
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Request()
    {
        try
        {
            if (HasAccess) return;
            MacNative.CGRequestPostEventAccess();
            Log.Info("已请求辅助功能权限；如果没有弹窗，请到 系统设置 → 隐私与安全性 → 辅助功能 中勾选 OneDesk");
        }
        catch (Exception ex)
        {
            Log.Warn("请求辅助功能权限失败: " + ex.Message);
        }
    }

    public static void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                ArgumentList = { "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility" },
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Log.Warn("打开辅助功能设置失败: " + ex.Message);
        }
    }
}
