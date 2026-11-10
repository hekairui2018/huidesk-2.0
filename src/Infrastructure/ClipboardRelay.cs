using System.Runtime.InteropServices;

namespace RemoteConnectHelper.V2.Infrastructure;

/// <summary>
/// 步E：接力用的剪贴板。
/// 装设备码 → 人按 Alt+V 粘进框 → 立刻换成验证码 → 结束/超时/取消时把用户原来的文本放回去。
/// 红线：密码只存在这个接力窗口里，收尾一律清掉，绝不进台账/日志/上报。
/// 用 Win32 直接写剪贴板（不走 WPF Clipboard），因为接力跑在后台线程，WPF 那套要求 STA + 会抛 ExternalException。
/// </summary>
public sealed class ClipboardRelay : IDisposable
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>接力开始前用户剪贴板里的文本（收尾要还回去）。</summary>
    public string? OriginalText { get; private set; }
    /// <summary>当下剪贴板里装的是我们放的东西（空串＝我们清空了）。</summary>
    public string? Holding { get; private set; }
    /// <summary>是否已经接管（保存过一次原文）。</summary>
    public bool Owned { get; private set; }

    /// <summary>读一次剪贴板文本（拿不到就 null）。</summary>
    public static string? Peek() => ClipboardReader.ReadTextOnce();

    /// <summary>写文本；剪贴板被别的程序占着时重试 8 次（每次 20ms）。</summary>
    public static bool Write(string text)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(20); continue; }
            try
            {
                EmptyClipboard();
                int chars = text.Length + 1;
                var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(chars * 2));
                if (handle == IntPtr.Zero) return false;
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) { GlobalFree(handle); return false; }
                try
                {
                    var buffer = new char[chars];
                    text.CopyTo(0, buffer, 0, text.Length);
                    Marshal.Copy(buffer, 0, pointer, chars);
                }
                finally { GlobalUnlock(handle); }
                // SetClipboardData 成功后内存所有权归系统，不能再 GlobalFree
                if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero) { GlobalFree(handle); return false; }
                return true;
            }
            finally { CloseClipboard(); }
        }
        return false;
    }

    /// <summary>接管：记下原文（只记第一次）。</summary>
    public void TakeOver()
    {
        if (Owned) return;
        OriginalText = Peek();
        Owned = true;
    }

    /// <summary>装一段内容进剪贴板；成功返回 true。</summary>
    public bool Hold(string text)
    {
        TakeOver();
        if (!Write(text)) return false;
        Holding = text;
        return true;
    }

    /// <summary>把自己装的东西撤掉，还原用户原文（原文没有就清空，绝不把密码留在剪贴板）。</summary>
    public void Release()
    {
        if (!Owned) { Holding = null; return; }
        var restore = OriginalText ?? "";
        Write(restore);
        Holding = null;
        OriginalText = null;
        Owned = false;
    }

    /// <summary>测试用：把原文设成指定值，模拟「用户剪贴板里本来有东西」。</summary>
    internal void SeedOriginal(string? text) { OriginalText = text; Owned = true; }

    public void Dispose() { try { Release(); } catch { /* 收尾失败不抛，避免盖掉真错误 */ } }
}
