# 按常规 Windows 应用方式安装 UsageBar，仅当前用户使用，无需管理员权限：
#   - 安装到 %LocalAppData%\Programs\UsageBar\UsageBar.exe
#   - 创建开始菜单和桌面快捷方式
#   - 在「设置 → 应用」及控制面板「程序和功能」中提供卸载入口
# 用法：
#   powershell -ExecutionPolicy Bypass -File windows\install.ps1              # 构建、安装并启动
#   powershell -ExecutionPolicy Bypass -File windows\install.ps1 -Exe UsageBar.exe  # 安装下载的 exe，无需源码
#   powershell -ExecutionPolicy Bypass -File windows\install.ps1 -NoDesktop   # 不创建桌面快捷方式
#   powershell -ExecutionPolicy Bypass -File windows\install.ps1 -NoRun       # 安装后不启动
#   powershell -ExecutionPolicy Bypass -File windows\install.ps1 -Uninstall   # 卸载程序，保留用户设置

param(
    [string]$Exe,
    [switch]$NoDesktop,
    [switch]$NoRun,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$AppExeName   = "UsageBar.exe"
$AppDir       = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Programs\UsageBar"
$AppExe       = Join-Path $AppDir $AppExeName
$StartMenuLnk = Join-Path ([Environment]::GetFolderPath("Programs")) "UsageBar.lnk"
$DesktopLnk   = Join-Path ([Environment]::GetFolderPath("Desktop"))   "UsageBar.lnk"
$RunKey       = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$UninstKey    = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UsageBar"

function Stop-InstalledUsageBar([string]$installedExe)
{
    foreach ($appProcess in @(Get-Process -Name UsageBar -ErrorAction SilentlyContinue))
    {
        if ($appProcess.Path -ne $installedExe) { continue }
        try
        {
            if (-not $appProcess.HasExited) { Stop-Process -InputObject $appProcess -Force -ErrorAction Stop }
        }
        catch { if (-not $appProcess.HasExited) { throw } }
        if (-not $appProcess.WaitForExit(5000)) { throw "等待 UsageBar 退出超时：$installedExe" }
    }
}

# 将共用卸载脚本写入程序目录，供「设置 → 应用」中的卸载入口调用，
# 删除源码或下载目录后仍可卸载。
$UninstallerScript = "function Stop-InstalledUsageBar {`r`n" + ${function:Stop-InstalledUsageBar}.ToString() + "`r`n}`r`n" + @'
# UsageBar 当前用户卸载脚本，也可从「设置 → 应用」调用。
$ErrorActionPreference = "Stop"
$AppDir       = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Programs\UsageBar"
$AppExe       = Join-Path $AppDir "UsageBar.exe"
$StartMenuLnk = Join-Path ([Environment]::GetFolderPath("Programs")) "UsageBar.lnk"
$DesktopLnk   = Join-Path ([Environment]::GetFolderPath("Desktop"))   "UsageBar.lnk"
$RunKey       = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

# 删除前核对绝对目录，并拒绝可能指向其他位置的重解析点。
$expectedAppDir = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Programs\UsageBar"))
if (Test-Path -LiteralPath $AppDir)
{
    $resolvedAppDir = (Resolve-Path -LiteralPath $AppDir).ProviderPath
    $appDirItem = Get-Item -LiteralPath $resolvedAppDir -Force
    if ([IO.Path]::GetFullPath($resolvedAppDir) -ne $expectedAppDir -or
        ($appDirItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "拒绝删除非预期安装目录：$resolvedAppDir" }
}

Stop-InstalledUsageBar $AppExe
$ws = New-Object -ComObject WScript.Shell
foreach ($lnk in @($StartMenuLnk, $DesktopLnk))
{
    if ((Test-Path -LiteralPath $lnk) -and $ws.CreateShortcut($lnk).TargetPath -eq $AppExe)
    {
        Remove-Item -LiteralPath $lnk -Force
    }
}
# 按完整程序路径匹配，避免删除名称相似目录中的便携版自启项。
$runVal = (Get-ItemProperty -LiteralPath $RunKey -Name UsageBar -ErrorAction SilentlyContinue).UsageBar
if ($runVal -eq "`"$AppExe`"" -or $runVal -eq $AppExe) { Remove-ItemProperty -LiteralPath $RunKey -Name UsageBar }
if (Test-Path -LiteralPath $AppDir) { Remove-Item -LiteralPath $resolvedAppDir -Recurse -Force }
Remove-Item -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UsageBar" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "UsageBar 已卸载。设置保留在 $env:APPDATA\UsageBar（不再需要可手动删除）。"
'@

if ($Uninstall)
{
    $tmp = Join-Path $env:TEMP "UsageBar-uninstall.ps1"
    Set-Content -Path $tmp -Value $UninstallerScript -Encoding UTF8
    & $tmp
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    return
}

# 通过 -Exe 指定程序、从源码构建，或使用本脚本旁的 exe。
if ($Exe)
{
    $srcExe = (Resolve-Path -LiteralPath $Exe).ProviderPath
}
else
{
    $srcExe = Join-Path $PSScriptRoot "dist\$AppExeName"
    if (Test-Path (Join-Path $PSScriptRoot "UsageBar\Program.cs"))
    {
        & (Join-Path $PSScriptRoot "build.ps1")
        if (-not (Test-Path $srcExe)) { throw "构建失败：$srcExe 不存在" }
    }
    elseif (-not (Test-Path $srcExe) -and (Test-Path (Join-Path $PSScriptRoot $AppExeName)))
    {
        $srcExe = Join-Path $PSScriptRoot $AppExeName
    }
    elseif (-not (Test-Path $srcExe))
    {
        throw "找不到 UsageBar.exe：用 -Exe 指定路径，或把 exe 放在本脚本旁边。"
    }
}

$wasInstalled = Test-Path -LiteralPath $AppExe -PathType Leaf
$runVal = (Get-ItemProperty -LiteralPath $RunKey -Name UsageBar -ErrorAction SilentlyContinue).UsageBar
$enableAutostart = -not $wasInstalled -or -not [string]::IsNullOrEmpty($runVal)
Stop-InstalledUsageBar $AppExe
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
if ($srcExe -ne $AppExe) { Copy-Item -LiteralPath $srcExe -Destination $AppExe -Force }
Unblock-File -LiteralPath $AppExe -ErrorAction SilentlyContinue   # 移除下载来源标记，避免安装文件触发 SmartScreen 提示。

function New-Shortcut($lnkPath, $target)
{
    $ws = New-Object -ComObject WScript.Shell
    $s = $ws.CreateShortcut($lnkPath)
    $s.TargetPath       = $target
    $s.WorkingDirectory = Split-Path $target
    $s.IconLocation     = "$target,0"
    $s.Description      = "UsageBar — AI CLI token 用量表"
    $s.Save()
}
New-Shortcut $StartMenuLnk $AppExe
if (-not $NoDesktop) { New-Shortcut $DesktopLnk $AppExe }

# 注册当前用户卸载入口，显示于「设置 → 应用」及「程序和功能」。
$version = (Get-Item $AppExe).VersionInfo.FileVersion
if (-not $version) { $version = "1.0" }
New-Item -Path $UninstKey -Force | Out-Null
Set-ItemProperty $UninstKey -Name DisplayName     -Value "UsageBar"
Set-ItemProperty $UninstKey -Name DisplayVersion  -Value $version
Set-ItemProperty $UninstKey -Name Publisher       -Value "zquickm"
Set-ItemProperty $UninstKey -Name DisplayIcon     -Value "$AppExe,0"
Set-ItemProperty $UninstKey -Name InstallLocation -Value $AppDir
Set-ItemProperty $UninstKey -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$AppDir\uninstall.ps1`""
Set-ItemProperty $UninstKey -Name NoModify        -Value 1 -Type DWord
Set-ItemProperty $UninstKey -Name NoRepair        -Value 1 -Type DWord
Set-ItemProperty $UninstKey -Name EstimatedSize   -Value ([int]((Get-Item $AppExe).Length / 1KB)) -Type DWord

Set-Content -Path (Join-Path $AppDir "uninstall.ps1") -Value $UninstallerScript -Encoding UTF8

# 首次安装开启自启，更新时保留用户已关闭的自启状态。
if ($enableAutostart)
{
    if (-not (Test-Path -LiteralPath $RunKey)) { New-Item -Path $RunKey | Out-Null }
    Set-ItemProperty -LiteralPath $RunKey -Name UsageBar -Value "`"$AppExe`""
}

if (-not $NoRun)
{
    $otherInstances = @(Get-Process -Name UsageBar -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $AppExe })
    if ($otherInstances.Count -gt 0) { Write-Host "其他目录的 UsageBar 仍在运行，请从其菜单退出后，再启动 $AppExe。" }
    else { Start-Process -FilePath $AppExe -WindowStyle Hidden }
}
Write-Host "已安装到 $AppDir"
Write-Host "已创建开始菜单和桌面快捷方式；可在 设置 → 应用 里卸载。"
