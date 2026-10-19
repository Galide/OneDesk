using System.Runtime.InteropServices;
using OneDesk.Core;
using static OneDesk.Platform.Win.NativeWin;

namespace OneDesk.Platform.Win;

public static class WinClipboard
{
    private const int MaxUtf8Bytes = 1_500_000;

    private static string Limit(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= MaxUtf8Bytes) return text;

        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, mid)) <= MaxUtf8Bytes) lo = mid;
            else hi = mid - 1;
        }

        if (lo > 0 && char.IsHighSurrogate(text[lo - 1])) lo--;
        return text[..lo];
    }

    public static string GetText()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(15);
                continue;
            }

            try
            {
                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return "";
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return "";
                try
                {
                    var text = Marshal.PtrToStringUni(pointer) ?? "";
                    return Limit(text);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        Log.Warn("读取剪贴板失败：剪贴板被其他程序占用");
        return "";
    }

    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        text = Limit(text);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(15);
                continue;
            }

            try
            {
                if (!EmptyClipboard()) return false;

                var bytes = (text.Length + 1) * 2;
                var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (handle == IntPtr.Zero) return false;

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }

                try
                {
                    var chars = text.ToCharArray();
                    Marshal.Copy(chars, 0, pointer, chars.Length);
                    Marshal.WriteInt16(pointer, chars.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        Log.Warn("写入剪贴板失败：剪贴板被其他程序占用");
        return false;
    }
}
