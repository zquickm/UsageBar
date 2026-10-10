using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace UsageBar
{
    static class RemoteTest
    {
        static int failures;
        static readonly DateTime Today = new DateTime(2026, 10, 10);

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name);
            if (!ok) failures++;
        }

        static Payload Sample(string date, string agent, string model, long tokens)
        {
            DayPoint day = new DayPoint(date);
            day.Agents[agent] = tokens;
            day.Models[model] = tokens;
            return new Payload { Days = new List<DayPoint> { day }, Total = tokens };
        }

        static void Reject(Action action, string name)
        {
            try { action(); Check(false, name); }
            catch (ArgumentException) { Check(true, name); }
            catch (FormatException) { Check(true, name); }
            catch (TargetInvocationException e)
            {
                Check(e.InnerException is FormatException || e.InnerException is OverflowException, name);
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Contains("--echo")) { Console.Write(new JavaScriptSerializer().Serialize(args.Skip(1).ToArray())); return 0; }
            if (args.Contains("--wait")) { Thread.Sleep(5000); return 0; }
            Type merge = typeof(Payload).Assembly.GetType("UsageBar.UsageMerge");
            Check(merge != null, "local and remote daily merge is available");
            if (merge == null) return 1;
            Func<Payload[], Payload> combine = delegate(Payload[] items)
            {
                return (Payload)merge.GetMethod("Combine").Invoke(null, new object[] { items, Today });
            };
            Payload local = Sample("2026-10-10", "zcode", "GLM-5.3", 100);
            Payload remote = Sample("2026-10-10", "ZCODE", "glm-5.3", 60);
            Payload previous = Sample("2026-10-09", "codex", "gpt-test", 20);
            Payload merged = combine(new[] { local, remote, previous });
            DayPoint last = merged.Days.Last();
            Check(last.Agents.Count == 1 && last.Agents["zcode"] == 160, "same tool sums ignoring case");
            Check(last.Models.Count == 1 && last.Models.Keys.Single() == "GLM-5.3" && last.Models["GLM-5.3"] == 160,
                "same model sums and keeps local spelling");
            Check(merged.Total == 160 && merged.Days[merged.Days.Count - 2].Agents["codex"] == 20,
                "dates stay separate and tray shows only today");
            Check(local.Days[0].Agents["zcode"] == 100, "merge does not modify source snapshots");
            Check(combine(new[] { local, remote }).Total == 160, "repeated merge never accumulates previous result");
            Check(combine(new[] { local, Sample("2026-10-10", "zcode", "GLM-other", 5) }).Days.Last().Models.Count == 2,
                "different model names never merge");
            Payload earlier = Sample("2026-10-09", "ZCODE", "glm-5.3", 30);
            Payload acrossDays = combine(new[] { local, earlier });
            Store store = new Store { Points = acrossDays.Days.Skip(acrossDays.Days.Count - 7).ToList(),
                KnownModels = new HashSet<string>(acrossDays.Days.SelectMany(d => d.Models.Keys), StringComparer.OrdinalIgnoreCase) };
            Settings selected = new Settings { VisibleModels = "GLM-5.3", ModelOrder = "GLM-5.3", Colors = "GLM-5.3=#123456" };
            Check(ViewLogic.Series(selected, store, "model").SequenceEqual(new[] { "GLM-5.3" }),
                "earlier remote spelling never hides the selected local model");
            Check(ViewLogic.Discovered(store, "agent").Count(n => n.Equals("zcode", StringComparison.OrdinalIgnoreCase)) == 1,
                "tool names stay unique across dates");
            selected.VisibleModels = "glm-5.3";
            Check(ViewLogic.ListSeries(selected, store, "model").SequenceEqual(new[] { "GLM-5.3" }),
                "saved selection and order match ignoring case");
            Check(ViewLogic.ColorFor(selected, "glm-5.3", "model", false).ToArgb() == Color.FromArgb(0x12, 0x34, 0x56).ToArgb(),
                "saved colors match ignoring case");
            CultureInfo culture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                Check(combine(new[] { local }).Total == 100, "usage dates do not follow regional calendars");
            }
            finally { Thread.CurrentThread.CurrentCulture = culture; }

            MethodInfo parser = typeof(Ccusage).GetMethod("ParseJSON");
            string json = "{\"daily\":[{\"period\":\"2026-10-10\",\"agents\":[{\"agent\":\"opencode\",\"totalTokens\":173,\"modelBreakdowns\":[{\"modelName\":\"glm-test\",\"inputTokens\":50,\"outputTokens\":20,\"cacheCreationTokens\":10,\"cacheReadTokens\":44}]}]}]}";
            Payload parsed = (Payload)parser.Invoke(null, new object[] { json, Today, false });
            Check(parsed.Total == 173 && parsed.Days.Last().Models["glm-test"] == 124,
                "agent total and model component accounting remain unchanged");
            Payload empty = (Payload)parser.Invoke(null, new object[] { "{\"daily\":[]}", Today, false });
            Check(empty.Total == 0 && empty.Days.Count == 3650, "empty report is successful zero usage");
            Reject(delegate { parser.Invoke(null, new object[] { "{}", Today, false }); }, "missing daily report is rejected");
            Reject(delegate { parser.Invoke(null, new object[] { "not json", Today, false }); }, "invalid JSON is rejected");
            Reject(delegate { parser.Invoke(null, new object[] { json.Replace("173", "-1"), Today, false }); }, "negative tokens are rejected");
            Reject(delegate { parser.Invoke(null, new object[] { json.Replace("173", "1.5"), Today, false }); }, "fractional tokens are rejected");
            Reject(delegate { parser.Invoke(null, new object[] { json.Replace("2026-10-10", "2026-13-40"), Today, false }); }, "invalid dates are rejected");
            Reject(delegate { parser.Invoke(null, new object[] { json.Replace("\"totalTokens\":173,", ""), Today, false }); }, "missing agent totals cannot overwrite cache with zero");
            Reject(delegate { parser.Invoke(null, new object[] { json.Replace("\"modelName\"", "\"badModelKey\""), Today, false }); }, "missing model names are rejected");
            Reject(delegate { parser.Invoke(null, new object[] { json.Replace("opencode", " "), Today, false }); }, "blank tool names are rejected");
            Reject(delegate { parser.Invoke(null, new object[] { json.Replace("glm-test", " "), Today, false }); }, "blank model names are rejected");

            string directory = Path.Combine(Path.GetTempPath(), "usagebar-remote-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string cache = Path.Combine(directory, "cache.json");
            try
            {
                Settings settings = new Settings();
                Type sourcesType = typeof(Payload).Assembly.GetType("UsageBar.RemoteSources");
                try
                {
                    Settings damaged = new Settings();
                    damaged.RemoteConnections.Add(null);
                    damaged.RemoteConnections.Add(new RemoteConnection { Alias = "server99" });
                    dynamic recovered = Activator.CreateInstance(sourcesType, damaged, Path.Combine(directory, "missing-cache.json"));
                    Check(recovered.States.Count == 1, "null connection config does not block local startup");
                }
                catch (Exception) { Check(false, "null connection config does not block local startup"); }
                dynamic sources = Activator.CreateInstance(sourcesType, settings, cache);
                dynamic state = sources.Add("server99", "linux");
                Reject(delegate { sources.Add("SERVER99", "linux"); }, "duplicate aliases are rejected");
                Reject(delegate { sources.Add("-oProxyCommand=bad", "linux"); }, "SSH option injection is rejected");
                Reject(delegate { sources.Add("server99 & whoami", "linux"); }, "shell injection is rejected");
                Reject(delegate { sources.Add("other", "bad-system"); }, "invalid remote platform is rejected");
                int request = sources.Begin(state);
                Check(sources.Begin(state) == -1, "only one request per machine can run");
                Check(sources.Complete(state, request, remote, null), "successful fetch is accepted");
                Check(((Payload)sources.Combine(local, Today)).Total == 160, "snapshot is included in combined usage");
                request = sources.Begin(state);
                sources.Complete(state, request, Sample("2026-10-10", " ", "glm-5.3", 0), null);
                Check(((Payload)sources.Combine(local, Today)).Total == 160 && state.Error != null,
                    "invalid snapshot cannot replace previous successful cache");
                request = sources.Begin(state);
                sources.Complete(state, request, null, "offline");
                Check(((Payload)sources.Combine(local, Today)).Total == 160 && state.Error == "offline",
                    "offline machine keeps previous successful data");
                dynamic restored = Activator.CreateInstance(sourcesType, settings, cache);
                Check(((Payload)restored.Combine(local, Today)).Total == 160, "cache survives restart");
                request = sources.Begin(state);
                sources.SetEnabled(state, false);
                Check(((Payload)sources.Combine(local, Today)).Total == 100, "disabled machine immediately leaves totals");
                Check(!sources.Complete(state, request, remote, null), "old request after disable is discarded");
                sources.SetEnabled(state, true);
                request = sources.Begin(state);
                sources.Remove(state);
                Check(!sources.Complete(state, request, remote, null) && ((Payload)sources.Combine(local, Today)).Total == 100,
                    "removed machine cannot be reintroduced by an old request");
                dynamic newer = sources.Add("server99", "linux");
                Check(((Payload)sources.Combine(local, Today)).Total == 100, "re-added alias does not revive removed cache");
                sources.Complete(newer, sources.Begin(newer), empty, null);
                Check(((Payload)sources.Combine(local, Today)).Total == 100, "successful empty report replaces old usage");
                dynamic windows = sources.Add("desktop", "windows");
                sources.Complete(windows, sources.Begin(windows), Sample("2026-10-10", "codex", "gpt-test", 7), null);
                Check(((Payload)sources.Combine(local, Today)).Total == 107, "multiple machines sum independently");
            }
            finally { Directory.Delete(directory, true); }
            CheckProcessAndScripts(args);
            if (args.Contains("--ui")) CheckForms();
            return failures == 0 ? 0 : 1;
        }

        static void CheckProcessAndScripts(string[] args)
        {
            string[] values = { "", "a b", "a\"b", "C:\\path with space\\", "a\\\"b", "中文别名" };
            Ccusage.RunResult argv = Ccusage.RunProcess(Assembly.GetExecutingAssembly().Location,
                "--echo " + String.Join(" ", values.Select(RemoteEngine.Quote)), 5000);
            string[] actual = new JavaScriptSerializer().Deserialize<string[]>(argv.Out);
            Check(argv.Code == 0 && actual.SequenceEqual(values), "process argument quoting preserves spaces, quotes, slash and Unicode");
            Ccusage.RunResult timeout = Ccusage.RunProcess(Assembly.GetExecutingAssembly().Location, "--wait", 100);
            Check(timeout.Code == -1 && timeout.Err == "超时", "process timeout terminates its own request");
            Check(RemoteEngine.Error(new Ccusage.RunResult { Err = "Permission denied (publickey)" }).Contains("认证失败"), "authentication failures are actionable");
            Check(RemoteEngine.Error(new Ccusage.RunResult { Err = "Host key verification failed" }).Contains("指纹"), "unknown host keys are not silently accepted");
            Check(RemoteEngine.Error(new Ccusage.RunResult { Err = "USAGEBAR_CCUSAGE_MISSING" }).Contains("未安装"), "missing remote engine is distinguished from zero usage");
            if (args.Contains("--live"))
            {
                Ccusage.RunResult live = RemoteEngine.Run(new RemoteConnection { Alias = "server99" }, UsageMerge.Today);
                Check(live.Code == 0, "real server99 SSH command succeeds: " + (live.Code == 0 ? "" : RemoteEngine.Error(live)));
                if (live.Code == 0)
                {
                    Payload report = Ccusage.ParseJSON(live.Out, UsageMerge.Today, false);
                    Check(report.Days.Count == 3650 && report.Total > 0, "real server returns a daily report with today's usage");
                    Console.WriteLine("server99 today=" + report.Total + " models=" + String.Join(",", report.Days.Last().Models.Keys));
                }
            }
            if (args.Contains("--windows-script"))
            {
                string home = Path.Combine(Path.GetTempPath(), "usagebar remote 用户 " + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(home);
                try
                {
                    string command = RemoteEngine.Command(new RemoteConnection { Alias = "desktop", System = "windows" }, UsageMerge.Today);
                    string prefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
                    string script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Substring(prefix.Length)));
                    script = script.Replace("$homePath=[Environment]::GetFolderPath('UserProfile')", "$homePath='" + home.Replace("'", "''") + "'");
                    Ccusage.RunResult run = Ccusage.RunProcess("powershell.exe", "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
                        + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), 30000);
                    Check(run.Code == 0, "Windows command executes with Unicode and spaces in home: " + run.Err);
                    if (run.Code == 0) Check(Ccusage.ParseJSON(run.Out, UsageMerge.Today, false).Total == 0, "empty Windows profile returns valid zero usage");
                }
                finally { Directory.Delete(home, true); }
            }
        }

        static T Field<T>(object owner, string name)
        {
            return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(owner);
        }

        static void CheckForms()
        {
            Application.EnableVisualStyles();
            Program.AppFontFamily = new FontFamily("Microsoft YaHei UI");
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                Program.Scale = scale;
                Settings settings = new Settings { FirstRunDone = true };
                RemoteSources sources = new RemoteSources(settings, Path.Combine(Path.GetTempPath(), "nonexistent-usagebar-test-cache.json"));
                RemoteState state = sources.Add("server99", "linux");
                state.Data = Sample("2026-10-10", "zcode", "GLM-5.3", 60);
                state.LastSuccessUtc = DateTime.UtcNow;
                RemoteConnectionsForm dialog = null;
                using (dialog = new RemoteConnectionsForm(sources, delegate { }, delegate { dialog.Rebind(); }))
                {
                    dialog.ClientSize = new Size(Program.Px(550), Program.Px(390));
                    dialog.Show();
                    Application.DoEvents();
                    DataGridView grid = Field<DataGridView>(dialog, "grid");
                    try
                    {
                        grid.CurrentCell = grid.Rows[0].Cells[0];
                        grid.BeginEdit(false);
                        grid.CurrentCell.Value = false;
                        grid.EndEdit();
                        Application.DoEvents();
                        Check(!state.Connection.Enabled, "checkbox disables without rebind exception scale=" + scale);
                    }
                    catch (Exception ex) { Check(false, "checkbox event: " + ex.GetType().Name); }
                    FlowLayoutPanel actions = (FlowLayoutPanel)dialog.Controls[0].Controls[2];
                    Check(actions.Controls.Cast<Control>().All(c => actions.ClientRectangle.Contains(c.Bounds)),
                        "connection controls fit at minimum width scale=" + scale);
                    using (Bitmap bitmap = new Bitmap(dialog.Width, dialog.Height))
                    {
                        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(Path.GetTempPath(), "usagebar-remote-dialog-" + scale.ToString(CultureInfo.InvariantCulture) + ".png"));
                    }
                    dialog.Hide();
                }
                // 只渲染独立测试窗体，避开真实托盘、注册表和自动刷新初始化。
                AppContext context = (AppContext)FormatterServices.GetUninitializedObject(typeof(AppContext));
                typeof(AppContext).GetField("Store").SetValue(context, new Store());
                typeof(AppContext).GetField("Settings").SetValue(context, settings);
                typeof(AppContext).GetField("Remotes").SetValue(context, sources);
                using (FlyoutForm flyout = new FlyoutForm(context))
                {
                    typeof(FlyoutForm).GetMethod("LayoutContent", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flyout, null);
                    flyout.Show();
                    Application.DoEvents();
                    Panel bottom = Field<Panel>(flyout, "bottomBar");
                    Button remoteButton = Field<Button>(flyout, "remoteBtn");
                    Check(bottom.ClientRectangle.Contains(remoteButton.Bounds), "remote button is fully inside bottom bar scale=" + scale);
                    Check(bottom.Controls.Cast<Control>().Where(c => c != remoteButton).All(c => !c.Bounds.IntersectsWith(remoteButton.Bounds)),
                        "remote button never overlaps existing controls scale=" + scale);
                    using (Bitmap bitmap = new Bitmap(flyout.Width, flyout.Height))
                    {
                        flyout.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        int ink = 0;
                        for (int y = 0; y < bitmap.Height; y += 2)
                            for (int x = 0; x < bitmap.Width; x += 2)
                                if (bitmap.GetPixel(x, y).R < 210) ink++;
                        Check(ink > 100, "flyout screenshot contains visible controls scale=" + scale);
                        bitmap.Save(Path.Combine(Path.GetTempPath(), "usagebar-remote-footer-" + scale.ToString(CultureInfo.InvariantCulture) + ".png"));
                    }
                    if (scale == 1f)
                    {
                        using (System.Windows.Forms.Timer close = new System.Windows.Forms.Timer { Interval = 100 })
                        {
                            close.Tick += delegate
                            {
                                RemoteConnectionsForm active = Field<RemoteConnectionsForm>(context, "remoteDialog");
                                if (active == null) return;
                                close.Stop();
                                Check(context.RemoteDialogOpen && flyout.Visible, "opening remote dialog keeps its flyout owner visible");
                                typeof(Form).GetMethod("OnDeactivate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flyout, new object[] { EventArgs.Empty });
                                Check(flyout.Visible, "flyout does not hide while remote dialog is open");
                                active.Close();
                            };
                            close.Start();
                            remoteButton.PerformClick();
                            Check(!context.RemoteDialogOpen, "remote dialog closes and clears its context reference");
                        }
                        typeof(Form).GetMethod("OnDeactivate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flyout, new object[] { EventArgs.Empty });
                        Check(!flyout.Visible, "flyout still hides on normal deactivation");
                    }
                    flyout.Hide();
                }
            }
        }
    }
}
