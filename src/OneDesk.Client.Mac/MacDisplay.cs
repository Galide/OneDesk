using System.Text;
using OneDesk.Core;
using static OneDesk.Client.MacNative;

namespace OneDesk.Client;

public sealed class MacDisplayInfo
{
    public uint Id { get; init; }
    public int Index { get; init; }
    public bool Builtin { get; init; }
    public CGRect Bounds { get; init; }
    public string Uuid { get; init; } = "";

    public string Label =>
        $"[{Index + 1}] {(Builtin ? "内建屏" : "外接屏")} {Bounds.Size.Width:0}x{Bounds.Size.Height:0}" +
        (string.IsNullOrEmpty(Uuid) ? "" : $" {Uuid}");

    public override string ToString() => Label;
}

public static class MacDisplay
{
    public static List<MacDisplayInfo> GetActive()
    {
        var result = new List<MacDisplayInfo>();
        try
        {
            var ids = new uint[32];
            var error = CGGetActiveDisplayList((uint)ids.Length, ids, out var count);
            if (error != 0)
            {
                Log.Warn("CGGetActiveDisplayList 失败: " + error);
                return result;
            }

            for (var i = 0; i < count && i < ids.Length; i++)
            {
                var id = ids[i];
                var bounds = CGDisplayBounds(id);
                result.Add(new MacDisplayInfo
                {
                    Id = id,
                    Index = i,
                    Builtin = CGDisplayIsBuiltin(id) != 0,
                    Bounds = bounds,
                    Uuid = GetUuid(id)
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举 macOS 显示器失败", ex);
        }

        return result;
    }

    public static MacDisplayInfo? Pick(string preferredUuid)
    {
        var displays = GetActive();
        if (displays.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredUuid))
        {
            var match = displays.FirstOrDefault(d =>
                string.Equals(d.Uuid, preferredUuid.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return displays.FirstOrDefault(d => !d.Builtin && d.Bounds.Size.Width > 0) ??
               displays.FirstOrDefault(d => d.Bounds.Size.Width > 0);
    }

    private static string GetUuid(uint display)
    {
        var uuid = CGDisplayCreateUUIDFromDisplayID(display);
        if (uuid == IntPtr.Zero) return "";
        IntPtr text = IntPtr.Zero;
        try
        {
            text = CFUUIDCreateString(IntPtr.Zero, uuid);
            if (text == IntPtr.Zero) return "";
            var buffer = new byte[128];
            if (!CFStringGetCString(text, buffer, buffer.Length, CFStringEncodingUtf8)) return "";
            var zero = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, zero < 0 ? buffer.Length : zero);
        }
        catch
        {
            return "";
        }
        finally
        {
            if (text != IntPtr.Zero) CFRelease(text);
            CFRelease(uuid);
        }
    }
}
