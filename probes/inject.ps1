$ErrorActionPreference = "Stop"
$log = "D:\Sluice\probes\probe.log"
function Say($m) {
  $line = "{0} [inject] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $m
  Write-Output $m
  Add-Content -Path $log -Value $line
}
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Inj {
  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)]
  public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)]
  public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public INPUTUNION u; }
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)]
  public struct POINT { public int x, y; }
  [DllImport("user32.dll")]
  public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
  public const uint LEFTDOWN = 0x02, LEFTUP = 0x04, KEYUP = 0x01;
  public const uint GA_ROOT = 2;
  public static bool ClickAt(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(150);
    var down = new INPUT(); down.type = 0; down.u.mi.dwFlags = LEFTDOWN;
    var up = new INPUT(); up.type = 0; up.u.mi.dwFlags = LEFTUP;
    var arr = new INPUT[]{ down, up };
    return SendInput(2, arr, Marshal.SizeOf(typeof(INPUT))) == 2;
  }
  public static bool PressF12() {
    var down = new INPUT(); down.type = 1; down.u.ki.wVk = 0x7B;
    var up = new INPUT(); up.type = 1; up.u.ki.wVk = 0x7B; up.u.ki.dwFlags = KEYUP;
    var arr = new INPUT[]{ down, up };
    return SendInput(2, arr, Marshal.SizeOf(typeof(INPUT))) == 2;
  }
}
"@
Say "injector v2 started"

$exe = "D:\Sluice\probes\ProbeApp\bin\Debug\net10.0-windows10.0.26100\ProbeApp.exe"
$weLaunched = $false
$p = Get-Process -Name "ProbeApp" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($p) {
  Say "reusing already-running ProbeApp"
} else {
  Start-Process -FilePath $exe | Out-Null
  $weLaunched = $true
  Say "ProbeApp launched"
}
$p = $null
foreach ($i in 1..40) {
  Start-Sleep -Milliseconds 250
  $p = Get-Process -Name "ProbeApp" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) { break }
}
if (-not $p) { Say "FAIL: ProbeApp window never appeared"; exit 1 }
Say ("window: hwnd=0x{0:X}  title='{1}'  pid={2}" -f $p.MainWindowHandle.ToInt64(), $p.MainWindowTitle, $p.Id)

$r = New-Object Inj+RECT
[Inj]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int](($r.Top + $r.Bottom) / 2)
Say ("rect: L={0} T={1} R={2} B={3} -> center ({4},{5})" -f $r.Left, $r.Top, $r.Right, $r.Bottom, $cx, $cy)

$pt = New-Object Inj+POINT; $pt.x = $cx; $pt.y = $cy
$hitRoot = [Inj]::GetAncestor([Inj]::WindowFromPoint($pt), [Inj]::GA_ROOT)
Say ("window-at-center root: 0x{0:X}  probe=0x{1:X}" -f $hitRoot.ToInt64(), $p.MainWindowHandle.ToInt64())
if ($hitRoot -ne $p.MainWindowHandle) { Say "SKIP: center point is not the probe window, refusing blind injection"; if ($weLaunched) { Stop-Process -Id $p.Id -Force }; exit 1 }

$fg = [Inj]::GetForegroundWindow()
Say ("foreground: 0x{0:X}  focused={1}" -f $fg.ToInt64(), ($fg -eq $p.MainWindowHandle))
$ok = [Inj]::ClickAt($cx, $cy)
Say ("injected click sent: {0}" -f $ok)
Start-Sleep -Milliseconds 400
if ($fg -eq $p.MainWindowHandle) {
  $ok2 = [Inj]::PressF12()
  Say ("injected F12 sent: {0}" -f $ok2)
} else {
  Say "keyboard skipped: probe window not foreground (protecting your active app)"
}
Say "injector done"
if ($weLaunched) { Start-Sleep -Seconds 2; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Say "probe process stopped" }
