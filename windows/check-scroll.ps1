# 编译并运行用量列表滚动检查（滚轮偏移、钳制边界、命中行映射）。
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = @(Get-ChildItem -LiteralPath "$PSScriptRoot\UsageBar" -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$out = Join-Path $env:TEMP 'usagebar-check-scroll.exe'
& $csc /nologo /target:exe /out:$out /main:UsageBar.ScrollTest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    /r:Microsoft.CSharp.dll $sources "$PSScriptRoot\check-scroll.cs"
if ($LASTEXITCODE -ne 0) { throw '测试编译失败' }
& $out
exit $LASTEXITCODE
