using System.Runtime.InteropServices;
using OneDesk.Core;
using static OneDesk.Host.Native;

namespace OneDesk.Host;

public sealed class MonitorInfo
{
    public int Index { get; init; }
    public string Description { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public bool Primary { get; init; }
    public int? CurrentInput { get; init; }

    public string Label => $"#{Index}  {Description}  ({DeviceName})" +
                           (Primary ? "  [主显示器]" : "") +
                           (CurrentInput is { } v ? $"  当前输入 {v}" : "");

    public override string ToString() => Label;
}

/// <summary>DDC/CI input-source switching through dxva2.dll.</summary>
public static class Ddc
{
    private const byte VcpInputSource = 0x60;

    public static List<MonitorInfo> ListMonitors(bool readInput = true)
    {
        var result = new List<MonitorInfo>();
        var handles = new List<IntPtr>();

        try
        {
            MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                handles.Add(hMonitor);
                return true;
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);

            var index = 0;
            foreach (var hMonitor in handles)
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMonitor, ref mi)) continue;
                var primary = (mi.dwFlags & 1) != 0;

                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                {
                    continue;
                }

                var physical = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical)) continue;

                try
                {
                    foreach (var pm in physical)
                    {
                        int? input = null;
                        if (readInput &&
                            GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, VcpInputSource, out _,
                                out var current, out _))
                        {
                            input = (int)(current & 0xFF);
                        }

                        result.Add(new MonitorInfo
                        {
                            Index = index++,
                            Description = (pm.szPhysicalMonitorDescription ?? "").Trim(),
                            DeviceName = (mi.szDevice ?? "").Trim(),
                            Primary = primary,
                            CurrentInput = input
                        });
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, physical);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举显示器失败", ex);
        }

        return result;
    }

    public static (bool Ok, string Message) SetInput(int monitorIndex, string nameContains, int input)
    {
        try
        {
            var target = FindMonitor(monitorIndex, nameContains);
            if (target is null)
            {
                var all = ListMonitors(false);
                return (false, all.Count == 0
                    ? "未找到支持 DDC/CI 的显示器"
                    : $"未找到指定的显示器（共 {all.Count} 个，索引 0-{all.Count - 1}）");
            }

            var (physical, count, monitor, matched) = target.Value;
            try
            {
                if (!SetVCPFeature(physical[matched].hPhysicalMonitor, VcpInputSource, (uint)input))
                {
                    return (false, "SetVCPFeature 失败，错误码 " + Marshal.GetLastWin32Error() +
                                   "（显示器可能不支持 DDC/CI 或处于待机）");
                }

                Log.Info($"显示器 #{monitor.Index} 输入切换为 {input} ({InputName(input)})");
                return (true, $"已切换到 {InputName(input)} ({input})");
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }
        }
        catch (Exception ex)
        {
            Log.Error("DDC 切换失败", ex);
            return (false, ex.Message);
        }
    }

    public static (int? Value, string Message) GetInput(int monitorIndex, string nameContains)
    {
        try
        {
            var target = FindMonitor(monitorIndex, nameContains);
            if (target is null) return (null, "未找到指定的显示器");
            var (physical, count, monitor, matched) = target.Value;
            try
            {
                if (!GetVCPFeatureAndVCPFeatureReply(physical[matched].hPhysicalMonitor, VcpInputSource, out _,
                        out var current, out _))
                {
                    return (null, "读取失败，错误码 " + Marshal.GetLastWin32Error());
                }

                var value = (int)(current & 0xFF);
                return (value, $"当前输入 {value} ({InputName(value)})");
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }
        }
        catch (Exception ex)
        {
            Log.Error("DDC 读取失败", ex);
            return (null, ex.Message);
        }
    }

    private static (PHYSICAL_MONITOR[] Physical, uint Count, MonitorInfo Monitor, int Matched)? FindMonitor(int index,
        string nameContains)
    {
        var handles = new List<IntPtr>();
        MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            handles.Add(hMonitor);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        var flatIndex = 0;
        foreach (var hMonitor in handles)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMonitor, ref mi)) continue;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) continue;
            var physical = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical)) continue;

            for (var i = 0; i < physical.Length; i++)
            {
                var description = (physical[i].szPhysicalMonitorDescription ?? "").Trim();
                var device = (mi.szDevice ?? "").Trim();
                var matches = !string.IsNullOrWhiteSpace(nameContains)
                    ? description.Contains(nameContains, StringComparison.OrdinalIgnoreCase) ||
                      device.Contains(nameContains, StringComparison.OrdinalIgnoreCase)
                    : flatIndex == index;

                if (matches)
                {
                    var info = new MonitorInfo
                    {
                        Index = flatIndex,
                        Description = description,
                        DeviceName = device,
                        Primary = (mi.dwFlags & 1) != 0
                    };
                    return (physical, count, info, i);
                }

                flatIndex++;
            }

            DestroyPhysicalMonitors(count, physical);
        }

        return null;
    }

    public static string InputName(int value)
    {
        foreach (var (name, v) in AppConfig.InputPresets)
        {
            if (v == value) return name;
        }

        return "自定义";
    }
}
