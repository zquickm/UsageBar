$ErrorActionPreference = 'Stop'
$script:checks = 0
function Assert([bool]$condition, [string]$message) {
    $script:checks++
    if (-not $condition) { throw "检查失败：$message" }
}
function Find-Assignment($ast, [string]$name) {
    return $ast.Find({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and $node.Left.VariablePath.UserPath -eq $name }, $true)
}
$sourcePath = Join-Path $PSScriptRoot 'install.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
Assert ($errors.Count -eq 0) '安装脚本可直接解析'
Assert ([bool]($tokens | Where-Object { $_.Text -like '*已安装到*' })) '直接读取源文件保留中文'
$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
Assert ($sourceBytes[0] -eq 0xEF -and $sourceBytes[1] -eq 0xBB -and $sourceBytes[2] -eq 0xBF) 'UTF-8 BOM 兼容 Windows PowerShell 5.1'

# 只定义停止函数并生成卸载脚本文本，不执行安装或卸载主体。
$stopAst = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-InstalledUsageBar' }, $true)
. ([ScriptBlock]::Create($stopAst.Extent.Text))
. ([ScriptBlock]::Create((Find-Assignment $ast 'UninstallerScript').Extent.Text))
$uninstallAst = [System.Management.Automation.Language.Parser]::ParseInput($UninstallerScript, [ref]$tokens, [ref]$errors)
Assert ($errors.Count -eq 0) '生成的卸载脚本可解析'
$generatedStopAst = $uninstallAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-InstalledUsageBar' }, $true)
Assert ($generatedStopAst.Body.ParamBlock.Parameters.Count -eq 1) '卸载脚本保留停止函数参数'
. ([ScriptBlock]::Create($generatedStopAst.Extent.Text))

# 所有进程查询和停止都使用内存模拟，不接触真实应用。
function Get-Process($Name, $ErrorAction) { return $script:fixtureProcesses }
function Stop-Process($InputObject, [switch]$Force, $ErrorAction) {
    $script:stoppedPaths += $InputObject.Path
    $InputObject.HasExited = $true
    if ($script:stopRace) { throw '模拟进程在停止前已退出' }
}
function New-FixtureProcess([string]$path, [bool]$waitResult = $true) {
    $process = [pscustomobject]@{ Path=$path; HasExited=$false; WaitCalls=0; WaitTimeout=0; WaitResult=$waitResult }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($timeout) $this.WaitCalls++; $this.WaitTimeout=$timeout; return $this.WaitResult }
    return $process
}
$fixtureDirectory = Join-Path $env:TEMP 'UsageBar-安装检查'
$AppExe = Join-Path $fixtureDirectory 'UsageBar.exe'
$portableExe = Join-Path $env:TEMP 'UsageBar-便携检查\UsageBar.exe'
$owned = New-FixtureProcess $AppExe
$portable = New-FixtureProcess $portableExe
$script:fixtureProcesses = @($owned,$portable)
$script:stoppedPaths = @()
$script:stopRace = $false
Stop-InstalledUsageBar $AppExe
Assert ($script:stoppedPaths.Count -eq 1 -and $script:stoppedPaths[0] -eq $AppExe) '只停止完整安装路径的进程'
Assert ($owned.WaitCalls -eq 1 -and $owned.WaitTimeout -eq 5000) '停止后等待退出'
Assert ($portable.WaitCalls -eq 0 -and -not $portable.HasExited) '不停止便携实例'
$script:fixtureProcesses = @(New-FixtureProcess $AppExe $false)
$timedOut = $false
try { Stop-InstalledUsageBar $AppExe } catch { $timedOut = $_.Exception.Message -like '*超时*' }
Assert $timedOut '退出超时阻止后续文件操作'
$owned = New-FixtureProcess $AppExe
$script:fixtureProcesses = @($owned)
$script:stopRace = $true
Stop-InstalledUsageBar $AppExe
Assert ($owned.WaitCalls -eq 1) '退出竞态仍等待完成'

$autostartExpression = (Find-Assignment $ast 'enableAutostart').Right.Extent.Text
foreach ($fixture in @(
    @{installed=$false;run=$null;enabled=$true},
    @{installed=$true;run=$null;enabled=$false},
    @{installed=$true;run=('"' + $AppExe + '"');enabled=$true}
)) {
    $wasInstalled = $fixture.installed
    $runVal = $fixture.run
    Assert ((. ([ScriptBlock]::Create($autostartExpression))) -eq $fixture.enabled) '首次安装和更新保留自启状态'
}
$runGuard = $uninstallAst.Find({ param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -like '*$runVal -eq*' }, $true).Clauses[0].Item1.Extent.Text
foreach ($fixture in @(
    @{run=('"' + $AppExe + '"');owned=$true},
    @{run=$AppExe;owned=$true},
    @{run=('"' + $fixtureDirectory + '-old\UsageBar.exe"');owned=$false}
)) {
    $runVal = $fixture.run
    Assert ((. ([ScriptBlock]::Create($runGuard))) -eq $fixture.owned) '卸载精确匹配自启程序路径'
}

# 只评估快捷方式与删除条件，文件查询和快捷方式对象均为模拟。
function Test-Path($LiteralPath) { return $script:fixtureLinkExists }
$linkGuard = $uninstallAst.Find({ param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -like '*CreateShortcut*' }, $true).Clauses[0].Item1.Extent.Text
$ws = [pscustomobject]@{}
$ws | Add-Member -MemberType ScriptMethod -Name CreateShortcut -Value { param($path) return [pscustomobject]@{TargetPath=$script:fixtureLinkTarget} }
$lnk = Join-Path $fixtureDirectory 'UsageBar.lnk'
foreach ($fixture in @(
    @{exists=$true;target=$AppExe;owned=$true},
    @{exists=$true;target=$portableExe;owned=$false},
    @{exists=$false;target=$AppExe;owned=$false}
)) {
    $script:fixtureLinkExists = $fixture.exists
    $script:fixtureLinkTarget = $fixture.target
    Assert ((. ([ScriptBlock]::Create($linkGuard))) -eq $fixture.owned) '仅删除属于安装程序的快捷方式'
}
$deleteGuard = $uninstallAst.Find({ param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -like '*ReparsePoint*' }, $true).Clauses[0].Item1.Extent.Text
$expectedAppDir = $fixtureDirectory
foreach ($fixture in @(
    @{path=$expectedAppDir;attributes=[IO.FileAttributes]::Directory;reject=$false},
    @{path=($fixtureDirectory + '-other');attributes=[IO.FileAttributes]::Directory;reject=$true},
    @{path=$expectedAppDir;attributes=([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint);reject=$true}
)) {
    $resolvedAppDir = $fixture.path
    $appDirItem = [pscustomobject]@{Attributes=$fixture.attributes}
    Assert ((. ([ScriptBlock]::Create($deleteGuard))) -eq $fixture.reject) '递归删除拒绝错误目录和重解析点'
}
$deleteCommands = $uninstallAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Remove-Item' }, $true)
foreach ($command in $deleteCommands) { Assert (($command.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -eq 'LiteralPath' }).Count -eq 1) '删除使用 LiteralPath' }
$launchGuard = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -like '*$otherInstances.Count*' }, $true)
Assert ($launchGuard.Clauses[0].Item2.Statements.Count -eq 1 -and $launchGuard.Clauses[0].Item2.Extent.Text -like '*Write-Host*') '便携实例运行时只提示手动退出'
Write-Output "安装脚本检查通过：$script:checks 项。源码和生成卸载脚本解析正常；未执行安装、卸载或真实进程操作。"