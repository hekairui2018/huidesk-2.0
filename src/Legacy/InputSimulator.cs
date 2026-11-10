using System.Runtime.InteropServices;
using System.Windows;

namespace RemoteConnectHelper.Automation;

/// <summary>鼠标点击 + Win32 键盘输入。文本按 Unicode 发送，避免输入法改写设备码或密码。</summary>
public static class InputSimulator
{
    internal static readonly UIntPtr Signature=new(0x4752444Bu);
    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint {public int X,Y;}
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key,uint mode);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public INPUTUNION Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    private const uint MOUSEEVENTF_LEFTUP = 0x04;
    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x02;
    private const uint KEYEVENTF_UNICODE = 0x04;

    private static INPUT Key(ushort vk, uint flags = 0, ushort character = 0) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = vk,
            ScanCode = character!=0?character:(ushort)MapVirtualKey(vk,0),
            Flags = flags | (vk is 0x23 or 0x24 or 0x2E?1u:0u),ExtraInfo=Signature } }
    };

    private static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "模拟输入失败，请确认远程软件可接收输入");
    }

    /// <summary>
    /// 把窗口拉到前台。不使用 AttachThreadInput——它会附加到当前前台线程，
    /// 若该线程（如白屏无响应的远控 UI）挂起，SetForegroundWindow 会永久阻塞。
    /// 改用 ShowWindow + SetForegroundWindow，失败时用一次 ALT 键绕过前台锁。
    /// </summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, 1); // SW_SHOWNORMAL
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() != hwnd)
        {
            keybd_event(VK_MENU, 0, 0,unchecked((IntPtr)(long)Signature.ToUInt64()));
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP,unchecked((IntPtr)(long)Signature.ToUInt64()));
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        Thread.Sleep(300);
    }

    /// <summary>在屏幕坐标处左键单击。</summary>
    public static void Click(int x, int y, int settleMs = 120)
    {
        // Inject movement as well as the click: SetCursorPos is not marked injected
        // and our temporary input protection correctly rejects that unmarked event.
        int left=GetSystemMetrics(76), top=GetSystemMetrics(77);
        int width=GetSystemMetrics(78), height=GetSystemMetrics(79);
        if(width<=0||height<=0)throw new InvalidOperationException("无法读取桌面范围，已停止点击");
        Send(new[]{new INPUT{Type=0,Data=new INPUTUNION{Mouse=new MOUSEINPUT{
            X=(int)Math.Clamp(((long)x-left)*65536/width+32768/width,0,65535),
            Y=(int)Math.Clamp(((long)y-top)*65536/height+32768/height,0,65535),
            Flags=0x0001|0x8000|0x4000,ExtraInfo=Signature}}}});
        Thread.Sleep(60);
        if(GetCursorPos(out var moved)&&(Math.Abs(moved.X-x)>2||Math.Abs(moved.Y-y)>2)){
            InputProtection.PlaceCursor(x,y,()=>{
                if(!SetCursorPos(x,y))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"鼠标定位失败");
                Thread.Sleep(60); // The low-level move callback can be delivered after SetCursorPos returns.
            });
            Thread.Sleep(30);
        }
        if(!GetCursorPos(out var actual)||Math.Abs(actual.X-x)>2||Math.Abs(actual.Y-y)>2)
            throw new InvalidOperationException($"鼠标未到达目标位置，停止点击 expected={x},{y} actual={actual.X},{actual.Y} desktop={left},{top},{width},{height} hook={InputProtection.LastMouseEvent}");
        Send(new[]{new INPUT{Type=0,Data=new INPUTUNION{Mouse=new MOUSEINPUT{Flags=MOUSEEVENTF_LEFTDOWN,ExtraInfo=Signature}}}});
        Thread.Sleep(30);
        Send(new[]{new INPUT{Type=0,Data=new INPUTUNION{Mouse=new MOUSEINPUT{Flags=MOUSEEVENTF_LEFTUP,ExtraInfo=Signature}}}});
        Thread.Sleep(settleMs);
    }

    public static void ClickRect(System.Windows.Rect r, int settleMs = 120)
        => Click((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2), settleMs);

    /// <summary>清空当前焦点输入框（Ctrl+A 全选 → Delete 一次删除；不再连发退格，省时间）。</summary>
    public static void ClearFocusedField()
    {
        // Leave the modifier down while custom editors process the shortcut.
        // A single batch can release Ctrl before an older OrayUI reads its state.
        Send(new[] { Key(0x11) });
        try{
            Thread.Sleep(60);
            Send(new[] { Key(0x41), Key(0x41, KEYEVENTF_KEYUP) });
            Thread.Sleep(60);
        }finally{Send(new[] { Key(0x11, KEYEVENTF_KEYUP) });}
        Thread.Sleep(60);
        Send(new[] { Key(0x2E) });
        Thread.Sleep(30);
        Send(new[] { Key(0x2E, KEYEVENTF_KEYUP) });
        Thread.Sleep(35);
    }

    /// <summary>向当前前台窗口发送一次 Esc（取消连接中/关弹窗的兜底）。</summary>
    public static void SendEscape()
    {
        Send(new[] { Key(0x1B), Key(0x1B, KEYEVENTF_KEYUP) });
        Thread.Sleep(120);
    }

    /// <summary>
    /// 步E：把剪贴板内容粘进当前焦点框。
    /// 接力里我们只负责「按一次 Ctrl+V」——框是人点的，位置我们不知道也不需要知道。
    /// </summary>
    public static void SendPaste()
    {
        Send(new[] { Key(0x11) });
        try
        {
            Thread.Sleep(50);
            Send(new[] { Key(0x56), Key(0x56, KEYEVENTF_KEYUP) });
            Thread.Sleep(70);
        }
        finally { Send(new[] { Key(0x11, KEYEVENTF_KEYUP) }); }
        Thread.Sleep(90);
    }

    /// <summary>步E：代发一次回车。真机已证：在向日葵识别码框里回车就是直接连接，不用 Tab、不用点按钮。</summary>
    public static void SendEnter()
    {
        Send(new[] { Key(0x0D), Key(0x0D, KEYEVENTF_KEYUP) });
        Thread.Sleep(140);
    }

    /// <summary>原样输入 UTF-16 字符，不走键盘布局或输入法，也无需对密码特殊字符转义。</summary>
    public static void TypeDigits(string text)
    {
        if(!text.All(char.IsAsciiDigit))throw new ArgumentException("只接受数字");
        foreach(char digit in text){
            Send(new[]{Key(digit)});Thread.Sleep(20);
            Send(new[]{Key(digit,KEYEVENTF_KEYUP)});Thread.Sleep(12);
        }
        Thread.Sleep(120);
    }
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new INPUT[checked(text.Length * 2)];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = Key(0, KEYEVENTF_UNICODE, text[i]);
            inputs[i * 2 + 1] = Key(0, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, text[i]);
        }
        Send(inputs);
        Thread.Sleep(120);
    }

    public static Rect? GetWindowRect(IntPtr hwnd)
    {
        if (GetWindowRect(hwnd, out var rc))
            return new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
        return null;
    }
}
