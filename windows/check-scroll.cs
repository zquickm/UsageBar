// 用量列表滚轮滚动的回归检查：偏移钳制边界 + 命中行映射 + 短列表不滚动。
// 与 check-remote.cs 同模式：和主源码一起编译，/main 指向本类。
using System;
using System.Reflection;
using System.Windows.Forms;

namespace UsageBar
{
    static class ScrollTest
    {
        static int failures;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name);
            if (!ok) failures++;
        }

        static void Scroll(SeriesListPanel panel, int delta)
        {
            typeof(SeriesListPanel).GetMethod("OnMouseWheel", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(panel, new object[] { new MouseEventArgs(MouseButtons.None, 0, 0, 0, delta) });
        }

        static int Offset(SeriesListPanel panel)
        {
            return (int)typeof(SeriesListPanel).GetField("scrollOffset", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
        }

        [STAThread]
        static int Main()
        {
            SeriesListPanel panel = new SeriesListPanel();
            for (int i = 0; i < 20; i++)
                panel.Rows.Add(new SeriesListPanel.Row { Name = "系列" + i, Today = i, Week = i });
            panel.Width = Program.Px(352);
            panel.Height = panel.AutoHeight;

            Check(panel.MaxScroll > 0, "20 行超过 200px 高度上限，存在可滚动内容");
            int lines = Math.Max(1, SystemInformation.MouseWheelScrollLines);
            int notches = (int)Math.Ceiling(panel.MaxScroll / (double)(lines * panel.RowHeight));

            for (int i = 0; i < notches; i++) Scroll(panel, -120);
            Check(Offset(panel) == panel.MaxScroll, "滚轮向下滚到底并钳制在 MaxScroll");

            for (int i = 0; i < notches + 2; i++) Scroll(panel, 120);
            Check(Offset(panel) == 0, "滚轮向上回到顶部并钳制在 0");

            MethodInfo rowAt = typeof(SeriesListPanel).GetMethod("RowAt", BindingFlags.NonPublic | BindingFlags.Instance);
            Scroll(panel, -120);
            int firstVisible = (int)rowAt.Invoke(panel, new object[] { panel.HeaderHeight + panel.RowHeight / 2 });
            Check(firstVisible == Offset(panel) / panel.RowHeight, "滚动后 RowAt 将屏幕 y 映射到滚入的行");

            for (int i = 0; i < notches; i++) Scroll(panel, 120);   // 回顶再做短列表场景
            panel.Rows.RemoveRange(0, 15);
            panel.Height = panel.AutoHeight;
            Scroll(panel, -120);
            Check(Offset(panel) == 0 && panel.MaxScroll == 0, "行数不足一屏时不滚动");

            Console.WriteLine(failures == 0 ? "scroll checks passed" : failures + " scroll checks failed");
            return failures == 0 ? 0 : 1;
        }
    }
}
