# 编译并运行远程用量测试；默认离线，可选实测 SSH 和 Windows 脚本，不修改真实设置。
param([switch]$Live, [switch]$WindowsScript, [switch]$UI)
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = @(Get-ChildItem -LiteralPath "$PSScriptRoot\UsageBar" -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$out = Join-Path $env:TEMP 'usagebar-check-remote.exe'
& $csc /nologo /target:exe /out:$out /main:UsageBar.RemoteTest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    /r:Microsoft.CSharp.dll $sources "$PSScriptRoot\check-remote.cs"
if ($LASTEXITCODE -ne 0) { throw '测试编译失败' }
$arguments = @()
if ($Live) { $arguments += '--live' }
if ($WindowsScript) { $arguments += '--windows-script' }
if ($UI) { $arguments += '--ui' }
Write-Output "远程测试选项：$($arguments -join ' ')"
& $out @arguments
exit $LASTEXITCODE
