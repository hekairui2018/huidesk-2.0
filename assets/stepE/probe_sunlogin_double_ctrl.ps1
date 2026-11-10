Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class WinProbe
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const byte VK_CONTROL = 0x11;

    public static List<string> WindowsOf(int pid)
    {
        var list = new List<string>();
        EnumWindows((h, p) =>
        {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid == (uint)pid)
            {
                var cls = new StringBuilder(256); GetClassName(h, cls, 256);
                var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
                list.Add(string.Format("hwnd=0x{0:X} visible={1} iconic={2} class={3} title={4}",
                    h.ToInt64(), IsWindowVisible(h), IsIconic(h), cls, txt));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr FirstVisible(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid == (uint)pid && IsWindowVisible(h) && !IsIconic(h))
            {
                var txt = new StringBuilder(8); GetWindowText(h, txt, 8);
                if (txt.Length > 0) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void DoubleCtrl(int gapMs, int betweenMs)
    {
        for (int i = 0; i < 2; i++)
        {
            keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(gapMs);
            keybd_event(VK_CONTROL, 0, 2, IntPtr.Zero);
            System.Threading.Thread.Sleep(betweenMs);
        }
    }
}
'@

$sunPid = (Get-Process AweSun -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).Id
if (-not $sunPid) { $sunPid = (Get-Process AweSun | Select-Object -First 1).Id }
Write-Output "AweSun pid=$sunPid"
Write-Output '--- 双击Ctrl 之前的窗口:'
[WinProbe]::WindowsOf($sunPid) | Select-Object -First 12

$main = [WinProbe]::FirstVisible($sunPid)
Write-Output "主窗 hwnd=0x$('{0:X}' -f $main.ToInt64())  前台=0x$('{0:X}' -f ([WinProbe]::GetForegroundWindow()).ToInt64())"
if ($main -ne [IntPtr]::Zero) { [WinProbe]::ShowWindow($main, [WinProbe]::SW_MINIMIZE) | Out-Null }
Start-Sleep -Milliseconds 600
Write-Output '--- 最小化后 前台窗口是否仍是向日葵:'
"foreground=0x$('{0:X}' -f ([WinProbe]::GetForegroundWindow()).ToInt64())"

Write-Output '--- 发送 双击Ctrl (160ms间隔):'
[WinProbe]::DoubleCtrl(40, 120)
Start-Sleep -Milliseconds 1000

$fg = [WinProbe]::GetForegroundWindow()
Write-Output "双击Ctrl 后前台 hwnd=0x$('{0:X}' -f $fg.ToInt64())"
Add-Type -TypeDefinition @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class Who {
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  public static string Describe(IntPtr h) {
    uint pid; GetWindowThreadProcessId(h, out pid);
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    return string.Format("pid={0} class={1} title={2} iconic={3}", pid, c, t, IsIconic(h));
  }
}
'@
Write-Output ('  -> ' + [Who]::Describe($fg))
Write-Output '--- 双击Ctrl 之后的窗口:'
[WinProbe]::WindowsOf($sunPid) | Select-Object -First 12
