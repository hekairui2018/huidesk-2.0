Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class P {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "(none)";
    uint pid; GetWindowThreadProcessId(h, out pid);
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    RECT r; GetWindowRect(h, out r);
    return string.Format("pid={0} class={1} title='{2}' iconic={3} rect={4},{5},{6},{7}", pid, c, t, IsIconic(h), r.L, r.T, r.R, r.B);
  }
  public static void DoubleCtrl(int holdMs, int betweenMs) {
    keybd_event(0x11, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(holdMs);
    keybd_event(0x11, 0, 2, IntPtr.Zero); System.Threading.Thread.Sleep(betweenMs);
    keybd_event(0x11, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(holdMs);
    keybd_event(0x11, 0, 2, IntPtr.Zero);
  }
  public static void CtrlAltK() {
    keybd_event(0x11, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 0, IntPtr.Zero);
    keybd_event(0x4B, 0, 0, IntPtr.Zero); keybd_event(0x4B, 0, 2, IntPtr.Zero);
    keybd_event(0x12, 0, 2, IntPtr.Zero); keybd_event(0x11, 0, 2, IntPtr.Zero);
  }
}
'@

function Shot([string]$path) {
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output "shot -> $path"
}

$out = Join-Path $env:TEMP 'gd20probe'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$sun = Get-Process AweSun | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$h = $sun.MainWindowHandle
Write-Output "AweSun pid=$($sun.Id) main=0x$('{0:X}' -f $h.ToInt64())"
Write-Output ("before: " + [P]::Describe($h))

[P]::ShowWindow($h, 9) | Out-Null
[P]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 900
Write-Output ("restored: " + [P]::Describe($h))
Write-Output ("foreground: " + [P]::Describe([P]::GetForegroundWindow()))

[System.Windows.Forms.Clipboard]::SetText("123456789")
Write-Output "clipboard=123456789"
Shot "$out\probe_sun_before.png"

foreach ($gap in 90, 150, 260) {
  [P]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 350
  [P]::DoubleCtrl(45, $gap)
  Start-Sleep -Milliseconds 1100
  Write-Output ("after double-ctrl gap=$gap foreground: " + [P]::Describe([P]::GetForegroundWindow()))
  Write-Output ("                              main: " + [P]::Describe($h))
  Shot "$out\probe_sun_after_$gap.png"
}

[P]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 300
[P]::CtrlAltK()
Start-Sleep -Milliseconds 1200
Write-Output ("after ctrl+alt+k foreground: " + [P]::Describe([P]::GetForegroundWindow()))
Shot "$out\probe_sun_after_ctrlaltk.png"
