# 使用 Windows 自带的 C# 编译器构建 UsageBar.exe，无需额外 SDK。
# 产物可在预装 .NET Framework 4.8 的 Windows 10/11 上运行。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File windows\build.ps1          # 构建
#   powershell -ExecutionPolicy Bypass -File windows\build.ps1 -Run     # 构建并启动

param([switch]$Run)

$ErrorActionPreference = "Stop"

$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found — .NET Framework 4.x is required to build." }
Write-Host "Compiler: $csc"

$src = Join-Path $PSScriptRoot "UsageBar\Program.cs"
$ico = Join-Path $PSScriptRoot "UsageBar\app.ico"   # 图标来自 ../Icon.iconset
$outDir = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir "UsageBar.exe"

& $csc /nologo /target:winexe /platform:anycpu /out:$out `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    /r:Microsoft.CSharp.dll `
    /win32icon:$ico `
    $src
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
Write-Host "Built: $out"

if ($Run) { Start-Process $out }
