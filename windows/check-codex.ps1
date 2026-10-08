# 检查子进程的 Codex 数据目录回退和显式值优先，不改用户注册表或会话文件。
$ErrorActionPreference = "Stop"

$testCs = @"
using System;
using System.IO;
using System.Reflection;

namespace UsageBar
{
    static class CodexEnvironmentTest
    {
        static int failures;

        static void CheckChildHome(string expected, string scenario)
        {
            Ccusage.RunResult result = Ccusage.Run("set CODEX_HOME", 5000);
            string[] lines = result.Out.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool ok = result.Code == 0 && Array.IndexOf(lines, "CODEX_HOME=" + expected) >= 0;
            Console.WriteLine((ok ? "PASS: " : "FAIL: ") + scenario);
            if (!ok) failures++;
        }

        static int Main()
        {
            string original = Environment.GetEnvironmentVariable("CODEX_HOME");
            string userHome = Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User);
            MethodInfo configure = typeof(Program).GetMethod("ConfigureCodexHome",
                BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", null);
                configure.Invoke(null, null);
                if (String.IsNullOrEmpty(userHome))
                    Console.WriteLine("SKIP: user CODEX_HOME is not configured");
                else
                    CheckChildHome(userHome, "missing process CODEX_HOME falls back to the user setting");

                string explicitHome = Path.Combine(Path.GetTempPath(), "usagebar-codex-explicit-" + Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("CODEX_HOME", explicitHome);
                configure.Invoke(null, null);
                CheckChildHome(explicitHome, "explicit process CODEX_HOME is preserved");
            }
            finally
            {
                // 只临时改变测试进程的环境变量，结束时还原。
                Environment.SetEnvironmentVariable("CODEX_HOME", original);
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
"@

$tmpCs = Join-Path $env:TEMP "usagebar-check-codex.cs"
$testCs | Out-File -FilePath $tmpCs -Encoding UTF8
$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found" }

$src = Join-Path $PSScriptRoot "UsageBar\Program.cs"
$out = Join-Path $env:TEMP "usagebar-check-codex.exe"
& $csc /nologo /target:exe /out:$out /main:UsageBar.CodexEnvironmentTest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    /r:Microsoft.CSharp.dll $src $tmpCs
if ($LASTEXITCODE -ne 0) { throw "check compile failed" }
& $out
exit $LASTEXITCODE
