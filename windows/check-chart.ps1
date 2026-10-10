# 检查共享动态轴、小系列可读性、半透明总量及曲线不下穿零轴。
# 脚本和生成的源码均用带签名的 UTF-8，兼容 PowerShell 5.1 的中文读取。
$ErrorActionPreference = "Stop"

$testCs = @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace UsageBar
{
    static class ChartScaleTest
    {
        static readonly string Total = "\u603B\u91CF";   // 总量
        static readonly Color TotalOnWhite = Color.FromArgb(105, 105, 105);   // 透明度为 150 的黑色叠加白底

        static int failures = 0;
        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS: " : "FAIL: ") + what);
            if (!ok) failures++;
        }

        static void Fill(out List<DayPoint> pts)
        {
            string[] days = { "10-01", "10-02", "10-03", "10-04", "10-05", "10-06", "10-07" };
            long[] big   = { 8000000, 9000000, 10000000, 12000000, 11000000, 13000000, 15000000 };
            long[] small = { 300000, 0, 250000, 300000, 0, 250000, 100000 };
            pts = new List<DayPoint>();
            for (int i = 0; i < 7; i++)
            {
                DayPoint dp = new DayPoint(days[i]);
                dp.Agents["zcode"] = big[i];
                dp.Agents["codex"] = small[i];
                pts.Add(dp);
            }
        }

        static ChartPanel MakeChart(List<DayPoint> pts, string[] names)
        {
            ChartPanel chart = new ChartPanel();
            chart.Points = pts;
            chart.SeriesNames = new List<string>(names);
            chart.ColorFor = delegate(string name)
            {
                if (name == "zcode") return Color.FromArgb(0, 0, 255);     // 纯蓝
                if (name == "codex") return Color.FromArgb(255, 0, 0);     // 纯红
                return Color.FromArgb(0, 0, 0);                            // 总量原色
            };
            chart.Size = new Size(352, 172);   // 缩放比例为 100% 时的图表尺寸
            return chart;
        }

        static Bitmap Render(ChartPanel chart)
        {
            Bitmap bmp = new Bitmap(chart.Width, chart.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                MethodInfo paint = typeof(ChartPanel).GetMethod("OnPaint",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                paint.Invoke(chart, new object[] {
                    new PaintEventArgs(g, new Rectangle(0, 0, chart.Width, chart.Height)) });
            }
            return bmp;
        }

        static int Count(Bitmap bmp, Color want, int tol)
        {
            int count = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (Math.Abs(c.R - want.R) <= tol && Math.Abs(c.G - want.G) <= tol
                        && Math.Abs(c.B - want.B) <= tol) count++;
                }
            return count;
        }

        // 颜色像素的垂直跨度可识别被上限压成水平线的总量。
        static int Span(Bitmap bmp, Color want, int tol)
        {
            int minY = int.MaxValue, maxY = -1;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (Math.Abs(c.R - want.R) <= tol && Math.Abs(c.G - want.G) <= tol
                        && Math.Abs(c.B - want.B) <= tol)
                    {
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            return maxY < 0 ? -1 : maxY - minY;
        }

        // 统计轴下方的系列像素，允许线宽和抗锯齿覆盖零轴附近。
        static int CountBelow(Bitmap bmp, Color[] want, int tol, int y0)
        {
            int count = 0;
            for (int y = y0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    foreach (Color w in want)
                        if (Math.Abs(c.R - w.R) <= tol && Math.Abs(c.G - w.G) <= tol
                            && Math.Abs(c.B - w.B) <= tol) { count++; break; }
                }
            return count;
        }

        static void Main()
        {
            // 固定字体和缩放比例，避免像素断言依赖当前桌面配置。
            Program.Scale = 1f;
            Program.AppFontFamily = FontFamily.GenericSansSerif;

            List<DayPoint> pts;
            Fill(out pts);

            ChartPanel a = MakeChart(pts, new[] { Total, "zcode", "codex" });
            Bitmap bmp = Render(a);
            string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "usagebar-chart-scale.png");
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("render A saved: " + png);
            int blue = Count(bmp, Color.FromArgb(0, 0, 255), 40);
            int red = Count(bmp, Color.FromArgb(255, 0, 0), 40);
            Console.WriteLine("line pixels: zcode=" + blue + " codex=" + red);
            Check(blue >= 300, "dominant series line renders");
            Check(red >= 100, "50x-smaller series line stays visible");
            int redSpan = Span(bmp, Color.FromArgb(255, 0, 0), 40);
            Console.WriteLine("small-series peak height above its zero days: " + redSpan + "px");
            Check(redSpan >= 20, "dynamic axis lifts the 50x-smaller series to a readable height");
            // 总量与主系列重合时会被覆盖，独立场景验证其颜色和形状。
            int under = CountBelow(bmp,
                new[] { Color.FromArgb(0, 0, 255), Color.FromArgb(255, 0, 0), TotalOnWhite }, 40, 156);
            Console.WriteLine("series pixels below the axis row: " + under);
            Check(under == 0, "monotone lines never sag below the axis (zero days stay on it)");

            // 仅选择总量时，上限仍需随总量的峰值变化。
            ChartPanel b = MakeChart(pts, new[] { Total });
            Bitmap bmp2 = Render(b);
            int gray2 = Count(bmp2, TotalOnWhite, 40);
            int span = Span(bmp2, TotalOnWhite, 40);
            Console.WriteLine("total-only: pixels=" + gray2 + " vertical span=" + span + "px");
            Check(gray2 >= 60 && span >= 55, "total-only view scales to its own peak (not flatlined)");

            // 隐藏主系列时，上限必须包含总量，防止总量被压在顶边。
            ChartPanel cPanel = MakeChart(pts, new[] { Total, "codex" });
            Bitmap bmp3 = Render(cPanel);
            string png3 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "usagebar-chart-total-codex.png");
            bmp3.Save(png3, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("render C saved: " + png3);
            int gray3 = Count(bmp3, TotalOnWhite, 40);
            int span3 = Span(bmp3, TotalOnWhite, 40);
            int red3 = Count(bmp3, Color.FromArgb(255, 0, 0), 40);
            Console.WriteLine("total+codex: total pixels=" + gray3 + " span=" + span3 + "px codex=" + red3);
            Check(gray3 >= 60 && span3 >= 25, "total line keeps its shape when the dominant series is hidden");
            Check(red3 >= 100, "small series line still visible next to the total");

            Environment.Exit(failures == 0 ? 0 : 1);
        }
    }
}
"@

$tmpCs = Join-Path $env:TEMP "usagebar-check-chart.cs"
$testCs | Out-File -FilePath $tmpCs -Encoding UTF8

$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found" }

$src = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'UsageBar') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$out = Join-Path $env:TEMP "usagebar-check-chart.exe"
& $csc /nologo /target:exe /out:$out /main:UsageBar.ChartScaleTest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    /r:Microsoft.CSharp.dll $src $tmpCs
if ($LASTEXITCODE -ne 0) { throw "check compile failed" }

& $out
exit $LASTEXITCODE
