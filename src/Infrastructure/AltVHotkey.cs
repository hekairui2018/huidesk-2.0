using System.Runtime.InteropServices;
using RemoteConnectHelper.Hotkey;

namespace RemoteConnectHelper.V2.Infrastructure;

/// <summary>
/// 步E：向日葵接力的触发器 —— 专用热键 <c>Alt+V</c>。
/// 为什么不用 Ctrl+V、不靠剪贴板序列号：
/// ① Ctrl+V 是全局粘贴，用户平时就在按，误触会把设备码粘到聊天窗口里；
/// ② 粘贴只是「读」剪贴板，不写，序列号根本不会变，判不出人按没按。
/// 所以用一个专用组合键当「我点好框了，可以粘」的信号，并用低级键盘钩子把它吞掉（不传给前台程序）。
/// 钩子必须装在会跑消息循环的线程上（WPF 的 UI 线程）；装不上就退到 RegisterHotKey（原子独占，同样会吞键）。
/// </summary>
public sealed class AltVHotkey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_V = 0x56;
    private const uint MOD_ALT = 0x0001;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdHookData { public uint VirtualKey, ScanCode, Flags, Time; public UIntPtr Extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private readonly HookProc _callback;          // 必须存字段：委托被回收，钩子就哑了
    private IntPtr _hook = IntPtr.Zero;
    private HotkeyManager? _fallback;
    private bool _swallowed;
    private volatile bool _altDown;
    private volatile bool _armed;
    private volatile bool _disposed;

    public AltVHotkey() => _callback = OnKeyboard;

    public bool HookInstalled => _hook != IntPtr.Zero;
    public bool FallbackRegistered { get; private set; }
    /// <summary>钩子或退路任一装上都算能收键。</summary>
    public bool Installed => _hook != IntPtr.Zero || FallbackRegistered;
    public string Detail { get; private set; } = "未安装";
    public int Captured => Volatile.Read(ref _captured);
    private int _captured;
    /// <summary>装好但没「上膛」时一律放行，不影响用户平时的 Alt+V。</summary>
    public bool Armed { get => _armed; set => _armed = value; }
    public event Action? Pressed;

    /// <summary>在会跑消息循环的线程上安装；WPF 主窗口场景请在 UI 线程调用。</summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero || FallbackRegistered) return true;
        try
        {
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
            if (_hook != IntPtr.Zero) { Detail = "低级键盘钩子已挂上（Alt+V 会被吞掉）"; return true; }
            Detail = "键盘钩子安装失败 err=" + Marshal.GetLastWin32Error();
        }
        catch (Exception ex) { Detail = "键盘钩子异常 " + ex.GetType().Name; }

        try
        {
            _fallback = new HotkeyManager();
            _fallback.Pressed += () => { if (_armed) Fire(); };
            FallbackRegistered = _fallback.Register(MOD_ALT, VK_V);
            Detail += FallbackRegistered ? "；已退到 RegisterHotKey(Alt+V)" : "；RegisterHotKey 也被别的程序占着";
        }
        catch (Exception ex) { Detail += "；RegisterHotKey 异常 " + ex.GetType().Name; }
        return FallbackRegistered;
    }

    /// <summary>若当前不在 UI 线程，把安装动作挪到 UI 线程（钩子回调只在装它的线程上派发）。</summary>
    public bool InstallOnDispatcher()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            if (dispatcher == null && System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread) == null)
                Detail = "当前线程没有消息循环，钩子收不到按键（请在 UI 线程安装）";
            return Install();
        }
        bool installed = false;
        dispatcher.Invoke(() => installed = Install());
        return installed;
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<KbdHookData>(lParam);
                bool down = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                uint key = data.VirtualKey;
                if (key is VK_MENU or VK_LMENU or VK_RMENU) _altDown = down;
                else if (key == VK_V)
                {
                    if (down)
                    {
                        if (ShouldCapture(_armed, _altDown, key, down))
                        {
                            _swallowed = true;
                            Interlocked.Increment(ref _captured);
                            ThreadPool.QueueUserWorkItem(_ => Fire());
                            return (IntPtr)1;   // 吞掉，前台程序看不到这个 V
                        }
                    }
                    else if (_swallowed)
                    {
                        _swallowed = false;
                        return (IntPtr)1;   // 抬键也吞掉，避免留下「按住的 V」
                    }
                }
            }
        }
        catch { /* 钩子里绝不能抛 */ }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Fire()
    {
        try { Pressed?.Invoke(); } catch { /* 接力线程自己兜底 */ }
    }

    /// <summary>判据单独拎出来，便于单测：只有「上膛 + Alt 按住 + V 按下」才吞。</summary>
    internal static bool ShouldCapture(bool armed, bool altDown, uint virtualKey, bool down)
        => armed && altDown && down && virtualKey == VK_V;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _armed = false;
        try { if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        try { _fallback?.Dispose(); } catch { }
        _fallback = null;
    }
}
