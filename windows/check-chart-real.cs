// 使用真实配色和七天数据，检查曲线形状、峰谷边界及不同尺寸和缩放比例的完整显示。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace UsageBar
{
    static class ChartRealTest
    {
        const string Total = "\u603B\u91CF";   // 总量

        // 保留应用的真实配色。
        static readonly Color Zcode = Color.FromArgb(0, 122, 255);
        static readonly Color Codex = Color.FromArgb(175, 82, 222);
        static readonly Color Dsh = Color.FromArgb(255, 149, 0);

        static ChartPanel MakeChart(List<DayPoint> pts, List<string> names)
        {
            ChartPanel chart = new ChartPanel();
            chart.Points = pts;
            chart.SeriesNames = names;
            chart.ColorFor = delegate(string name)
            {
                if (name == "zcode") return Zcode;
                if (name == "codex") return Codex;
                if (name == "dsh") return Dsh;
                return Color.FromArgb(40, 40, 45);   // 总量默认色
            };
            chart.Size = new Size(352, 172);
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

        static int failures = 0;
        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS: " : "FAIL: ") + what);
            if (!ok) failures++;
        }

        static void CheckMonotonePath()
        {
            // 不均匀峰谷会暴露相邻斜率变号时的过冲，不能只检查整条曲线的最高值。
            PointF[][] cases = {
                new[] { new PointF(0, 150), new PointF(50, 40), new PointF(100, 8), new PointF(150, 150) },
                new[] { new PointF(0, 8), new PointF(50, 120), new PointF(100, 150), new PointF(150, 8) },
                new[] { new PointF(0, 150), new PointF(50, 150), new PointF(100, 8), new PointF(150, 8) }
            };
            MethodInfo method = typeof(ChartPanel).GetMethod("MonotonePath",
                BindingFlags.NonPublic | BindingFlags.Static);
            for (int c = 0; c < cases.Length; c++)
            {
                PointF[] pts = cases[c];
                bool bounded = true;
                float worst = 0;
                using (GraphicsPath path = (GraphicsPath)method.Invoke(null, new object[] { pts }))
                {
                    path.Flatten(null, 0.01f);
                    int interval = 0;
                    foreach (PointF p in path.PathPoints)
                    {
                        while (interval < pts.Length - 2 && p.X > pts[interval + 1].X) interval++;
                        float lower = Math.Min(pts[interval].Y, pts[interval + 1].Y);
                        float upper = Math.Max(pts[interval].Y, pts[interval + 1].Y);
                        float excess = Math.Max(lower - p.Y, p.Y - upper);
                        worst = Math.Max(worst, excess);
                        if (excess > 0.02f) bounded = false;
                    }
                }
                Console.WriteLine("path case " + c + " maximum interval overshoot=" + worst + "px");
                Check(bounded, "curve stays inside adjacent endpoints for peak/valley case " + c);
            }
        }

        static Rectangle InkBounds(Bitmap bmp, Rectangle area)
        {
            int left = bmp.Width, top = bmp.Height, right = -1, bottom = -1;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    // 日期是灰色，排除更浅的网格和白色背景。
                    if (c.R < 210 && c.G < 210 && c.B < 215 && Math.Abs(c.R - c.G) < 6)
                    {
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
                }
            return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        static int FirstSeriesRow(Bitmap bmp)
        {
            Color[] colors = { Zcode, Codex, Dsh };
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    foreach (Color want in colors)
                        if (Math.Abs(c.R - want.R) <= 40 && Math.Abs(c.G - want.G) <= 40
                            && Math.Abs(c.B - want.B) <= 40) return y;
                }
            return -1;
        }

        static void CheckLayout(List<DayPoint> pts, List<string> names)
        {
            Size[] sizes = { new Size(220, 110), new Size(352, 172), new Size(700, 340) };
            float[] scales = { 1f, 1.25f, 1.5f, 2f };
            MethodInfo move = typeof(ChartPanel).GetMethod("OnMouseMove",
                BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (float scale in scales)
            {
                Program.Scale = scale;
                using (ChartPanel chart = MakeChart(pts, names))
                using (ChartPanel dates = MakeChart(pts, new List<string>()))
                using (Font font = Program.UiFont(10))
                using (Bitmap reference = new Bitmap(Program.Px(100), font.Height + Program.Px(10)))
                {
                    using (Graphics g = Graphics.FromImage(reference))
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(150, 120, 120, 125)))
                    {
                        g.Clear(Color.White);
                        g.DrawString(Fmt.CnDate(pts[0].Date), font, brush, 0, 0);
                    }
                    int glyphH = InkBounds(reference, new Rectangle(Point.Empty, reference.Size)).Height;
                    foreach (Size size in sizes)
                    {
                        chart.Size = dates.Size = size;
                        chart.HoverIndex = -1;
                        string scenario = size.Width + "x" + size.Height + " scale=" + scale;
                        using (Bitmap bmp = Render(chart))
                        {
                            int top = FirstSeriesRow(bmp);
                            int plotH = size.Height - Math.Max(Program.Px(14), font.Height) - Program.Px(4);
                            int minTop = (int)Math.Floor(Math.Max(Program.Px(8), plotH * 0.06f)
                                - 1.25f * scale - 1);
                            Check(top >= minTop, scenario + " leaves space above the peak");
                            int below = size.Height - Math.Max(Program.Px(14), font.Height)
                                - Program.Px(4) - Program.Px(2) + (int)Math.Ceiling(1.25f * scale) + 2;
                            Check(CountBelow(bmp, new[] { Zcode, Codex, Dsh }, 40, below) == 0,
                                scenario + " keeps series above the date labels");
                        }
                        using (Bitmap bmp = Render(dates))
                        {
                            int y = Math.Max(0, size.Height - font.Height - Program.Px(8));
                            Rectangle left = InkBounds(bmp, new Rectangle(0, y, size.Width / 2, size.Height - y));
                            Rectangle right = InkBounds(bmp, new Rectangle(size.Width / 2, y,
                                size.Width - size.Width / 2, size.Height - y));
                            Check(!left.IsEmpty && !right.IsEmpty && left.Left > 0 && right.Right < size.Width
                                && left.Bottom < size.Height && right.Bottom < size.Height
                                && left.Height >= glyphH && right.Height >= glyphH,
                                scenario + " shows unclipped first/last dates with bottom space");
                        }
                        bool hover = true;
                        int reported = -1;
                        chart.OnHover = delegate(int index) { reported = index; };
                        for (int i = 0; i < pts.Count; i++)
                        {
                            int x = Program.Px(10) + (int)Math.Round((size.Width - 2 * Program.Px(10))
                                * (double)i / (pts.Count - 1));
                            move.Invoke(chart, new object[] { new MouseEventArgs(MouseButtons.None, 0, x, 0, 0) });
                            hover &= chart.HoverIndex == i && reported == i;
                        }
                        move.Invoke(chart, new object[] { new MouseEventArgs(MouseButtons.None, 0, -10, 0, 0) });
                        hover &= chart.HoverIndex == 0;
                        move.Invoke(chart, new object[] { new MouseEventArgs(MouseButtons.None, 0, size.Width + 10, 0, 0) });
                        hover &= chart.HoverIndex == pts.Count - 1;
                        Check(hover, scenario + " maps all seven days and outside edges to the correct hover index");
                    }
                }
            }
            Program.Scale = 1f;
        }

        static void CheckEmptyAndZero(List<DayPoint> pts)
        {
            using (ChartPanel empty = MakeChart(new List<DayPoint>(), new List<string>()))
            using (Bitmap bmp = Render(empty))
                Check(Count(bmp, Color.FromArgb(177, 177, 180), 20) > 0, "empty data renders its placeholder");
            List<DayPoint> zero = new List<DayPoint>();
            foreach (DayPoint p in pts) zero.Add(new DayPoint(p.Date));
            using (ChartPanel chart = MakeChart(zero, new List<string>(new[] { "zcode" })))
            using (Bitmap bmp = Render(chart))
                Check(Count(bmp, Zcode, 40) >= chart.Width / 2, "all-zero data keeps its visible baseline");
        }

        static void Main()
        {
            Program.Scale = 1f;
            try { Program.AppFontFamily = new FontFamily("Microsoft YaHei UI"); }
            catch (ArgumentException) { Program.AppFontFamily = FontFamily.GenericSansSerif; }
            Console.WriteLine("font family: " + Program.AppFontFamily.Name);

            CheckMonotonePath();

            string[] dates = { "2026-10-02", "2026-10-03", "2026-10-04", "2026-10-05", "2026-10-06", "2026-10-07", "2026-10-08" };
            long[] zcode = { 0, 0, 0, 0, 80044334, 98262386, 1890141 };
            long[] codex = { 14228352, 2592282, 0, 0, 32314, 32424, 195919 };
            long[] dsh   = { 7070207, 0, 0, 0, 0, 0, 0 };
            List<DayPoint> pts = new List<DayPoint>();
            for (int i = 0; i < 7; i++)
            {
                DayPoint dp = new DayPoint(dates[i]);
                dp.Agents["zcode"] = zcode[i];
                dp.Agents["codex"] = codex[i];
                dp.Agents["dsh"] = dsh[i];
                pts.Add(dp);
            }
            List<string> names = new List<string>(new[] { Total, "zcode", "codex", "dsh" });

            // 完整四系列场景。
            ChartPanel chart = MakeChart(pts, names);
            Bitmap bmp = Render(chart);
            string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "usagebar-real-fixed.png");
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("render A saved: " + png);

            int blue = Count(bmp, Zcode, 40);
            int purple = Count(bmp, Codex, 40);
            int purpleSpan = Span(bmp, Codex, 40);
            int orange = Count(bmp, Dsh, 40);
            int orangeSpan = Span(bmp, Dsh, 40);
            Console.WriteLine("A: zcode=" + blue + " codex=" + purple + " span=" + purpleSpan
                + " dsh=" + orange + " span=" + orangeSpan);
            Check(blue >= 200, "zcode towers render (and stay visible under the translucent total)");
            Check(purple >= 100 && purpleSpan >= 30, "codex camel renders at readable height");
            Check(orange >= 60 && orangeSpan >= 15, "dsh single-day spike renders at readable height");
            int under = CountBelow(bmp, new[] { Zcode, Codex, Dsh }, 40, 156);
            Console.WriteLine("series pixels below the axis row: " + under);
            Check(under == 0, "monotone lines never sag below the axis");

            // 移除总量后对比像素数，检查重合的系列线是否被总量遮盖。
            ChartPanel noTotal = MakeChart(pts, new List<string>(new[] { "zcode", "codex", "dsh" }));
            Bitmap noTotalBmp = Render(noTotal);
            int blueNoTotal = Count(noTotalBmp, Zcode, 40);
            Console.WriteLine("B: zcode without total = " + blueNoTotal + " (with total = " + blue + ")");
            Check(blue >= blueNoTotal / 2, "translucent total does not erase the line it coincides with");

            // 隐藏主系列后，总量仍应保持形状；半透明默认色叠在白底上约为灰色。
            ChartPanel sub = MakeChart(pts, new List<string>(new[] { Total, "codex" }));
            Bitmap subBmp = Render(sub);
            string subPng = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "usagebar-real-total-codex.png");
            subBmp.Save(subPng, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("render C saved: " + subPng);
            int gray = Count(subBmp, Color.FromArgb(129, 129, 131), 28);
            int graySpan = Span(subBmp, Color.FromArgb(129, 129, 131), 28);
            int subPurple = Count(subBmp, Codex, 40);
            Console.WriteLine("C: total px=" + gray + " span=" + graySpan + " codex px=" + subPurple);
            Check(gray >= 40 && graySpan >= 90, "total line keeps its shape beside a small series");
            Check(subPurple >= 100, "codex line still visible next to the total");

            CheckLayout(pts, names);
            CheckEmptyAndZero(pts);

            Environment.Exit(failures == 0 ? 0 : 1);
        }
    }
}
