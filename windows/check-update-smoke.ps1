# Smoke check: click 检查更新 in the flyout and verify the process survives.
# Before the ui.Post fix the app died within ~2s of the click (pool-thread
# Controls.Add crash). Pass = process still alive 10s after the click.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);
    public static List<IntPtr> WindowsOf(uint wantPid) {
        List<IntPtr> result = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr lp) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == wantPid && IsWindowVisible(h)) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
    }
}
"@

$exe = "F:\UsageBar\windows\dist\UsageBar.exe"
$env:USAGEBAR_AUTOSHOW = "1"
$p = Start-Process $exe -PassThru
Write-Host "started pid=$($p.Id)"

$pid2 = [uint32]$p.Id
$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "检查更新")
$target = $null

for ($try = 0; $try -lt 10 -and -not $target; $try++) {
    Start-Sleep -Seconds 2
    $wins = [Win]::WindowsOf($pid2)
    Write-Host "try $try : visible windows = $($wins.Count)"
    foreach ($h in $wins) {
        $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
        $found = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
        foreach ($e in $found) {
            $rect = $e.Current.BoundingRectangle
            Write-Host ("  candidate: class={0} type={1} rect={2}" -f `
                $e.Current.ClassName, $e.Current.ControlType.ProgrammaticName, $rect)
            if ($rect.Width -gt 0 -and -not $target) { $target = $e }
        }
    }
}

if (-not $target) { Write-Host "FAIL: on-screen 检查更新 button not found"; Stop-Process -Id $p.Id -Force; exit 1 }

$pt = $target.GetClickablePoint()
Write-Host ("clicking at {0},{1}" -f [int]$pt.X, [int]$pt.Y)
[Win]::Click([int]$pt.X, [int]$pt.Y)
Start-Sleep -Seconds 10

$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if ($alive) {
    Write-Host "PASS: process alive 10s after click"
    Stop-Process -Id $p.Id -Force
    exit 0
} else {
    Write-Host "FAIL: process died after click"
    exit 1
}
