# 使用真实数据检查图表，将 check-chart-real.cs 与 Program.cs 一起编译。
$ErrorActionPreference = "Stop"
$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found" }
$src  = Join-Path $PSScriptRoot "UsageBar\Program.cs"
$test = Join-Path $PSScriptRoot "check-chart-real.cs"
$out  = Join-Path $env:TEMP "usagebar-check-real.exe"
& $csc /nologo /target:exe /out:$out /main:UsageBar.ChartRealTest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    /r:Microsoft.CSharp.dll $src $test
if ($LASTEXITCODE -ne 0) { throw "check compile failed" }
& $out
exit $LASTEXITCODE
