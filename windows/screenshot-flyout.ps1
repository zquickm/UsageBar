# Pop the running UsageBar flyout WITHOUT spawning a second instance (a second
# instance's brief window steals focus -> flyout Deactivate-closes -> the
# screenshot catches whatever is behind it). Broadcast the registered
# ShowFlyout message directly, then capture the window quickly.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern ushort RegisterWindowMessageW(string m);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static List<IntPtr> WindowsOf(uint wantPid) {
        List<IntPtr> result = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr lp) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == wantPid && IsWindowVisible(h)) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
"@

$p = Get-Process -Name UsageBar -ErrorAction Stop | Select-Object -First 1
"pid=$($p.Id)"

$msg = [Win]::RegisterWindowMessageW("UsageBar.ShowFlyout")
"msg=$msg"
[Win]::PostMessageW([IntPtr]0xFFFF, $msg, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 1200     # show + databind (cached data is instant)

$wins = [Win]::WindowsOf([uint32]$p.Id)
"visible windows: $($wins.Count)"
$target = [IntPtr]::Zero; $tr = New-Object Win+RECT
foreach ($h in $wins) {
    $r = New-Object Win+RECT
    [Win]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    "  hwnd=$h rect=$($r.L),$($r.T) ${w}x${ht}"
    if ($target -eq [IntPtr]::Zero -and $w -gt 300 -and $ht -gt 300) { $target = $h; $tr = $r }
}
if ($target -eq [IntPtr]::Zero) { throw "flyout window not found" }

$w = $tr.R - $tr.L; $ht = $tr.B - $tr.T
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($tr.L, $tr.T, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$out = Join-Path $PSScriptRoot "flyout-live.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"saved: $out"
