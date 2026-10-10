// UsageBar for Windows — system tray app for AI CLI token usage: tray icon +
// click-to-open flyout panel (Razer Synapse style).
// Windows counterpart of Sources/UsageBar/main.swift: same data flow (ccusage daily
// --offline --json --by-agent, 100% local), same views (7-day chart, by-tool /
// by-model series, colors, order), adapted to Windows conventions:
//   - the "menu bar number" becomes a small window embedded in the taskbar next to
//     the tray (the TrafficMonitor pattern — tray icons are square bitmaps, text
//     like 12.3万 needs a real window), with graceful fallback to tray-only
//   - the popover becomes a borderless flyout that closes on Esc / outside click
//   - autostart = HKCU Run key, settings = %APPDATA%\UsageBar\settings.json
//   - 首次启动创建开始菜单和桌面快捷方式；windows/install.ps1 提供完整用户安装和卸载入口
//   - 重复启动唤起已运行的面板
// Build: powershell -File windows\build.ps1   (uses the csc.exe shipped with
// Windows; the exe runs on any Win10/11 — .NET Framework 4.8 is preinstalled)
// Keep the code C# 5 compatible: the in-box compiler is C# 5 (no $"", no ?.).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

// 资源管理器和安装器读取的版本信息（单文件构建，程序集属性就地声明；
// 与 AppVersion、version.env 三处同步更新）
[assembly: AssemblyTitle("UsageBar")]
[assembly: AssemblyProduct("UsageBar")]
[assembly: AssemblyDescription("AI CLI token 用量托盘表（ccusage 引擎）")]
[assembly: AssemblyCompany("zquickm")]
[assembly: AssemblyFileVersion("1.2.2")]

namespace UsageBar
{
    static class Program
    {
        public const string AppVersion = "1.2.2";  // 与 version.env 的 MARKETING_VERSION 保持一致
        public static float Scale = 1f;
        public static FontFamily AppFontFamily;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "UsageBar.Windows.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 重复启动时唤起现有面板。
                    Win32.PostMessageW(Win32.HWND_BROADCAST,
                        Win32.RegisterWindowMessageW("UsageBar.ShowFlyout"), IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                ConfigureCodexHome();
                try { SetProcessDPIAware(); } catch { }
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;  // TLS 1.2
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(
                    delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                    {
                        Log("ThreadException: " + e.Exception);
                    });
                AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(
                    delegate(object s, UnhandledExceptionEventArgs e)
                    {
                        Log("UnhandledException: " + e.ExceptionObject);
                    });
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    Scale = g.DpiX / 96f;
                    if (Scale <= 0) Scale = 1f;
                }
                AppFontFamily = ResolveFontFamily(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" });
                Application.Run(new AppContext());
            }
        }

        static void ConfigureCodexHome()
        {
            // 构建进程可能缺少已保存的 Codex 目录，启动时补读用户配置，供所有子进程继承。
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
                Environment.SetEnvironmentVariable("CODEX_HOME",
                    Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User));
        }

        static FontFamily ResolveFontFamily(string[] candidates)
        {
            foreach (string name in candidates)
            {
                try { return new FontFamily(name); } catch { }
            }
            return FontFamily.GenericSansSerif;
        }

        public static Font UiFont(float px)
        {
            return new Font(AppFontFamily, px * Scale, GraphicsUnit.Pixel);
        }

        public static int Px(double v) { return (int)Math.Round(v * Scale); }

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UsageBar.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }

    // MARK: - Number formatting (same rules as the mac app)

    static class Fmt
    {
        public static string Human(long n)
        {
            double d = n;
            if (d >= 1e8) return ZhTrim(String.Format("{0:0.00}亿", d / 1e8));
            if (d >= 1e4) return ZhTrim(String.Format("{0:0.0}万", d / 1e4));
            return n.ToString();
        }

        public static string ZhTrim(string s)
        {
            return Regex.Replace(s, @"\.?0+(?=[亿万]$)", "");
        }

        // "10.06" — month without pad, day padded (same as the mac popover x labels)
        public static string CnDate(string iso)
        {
            string[] parts = iso.Split('-');
            int m, d;
            if (parts.Length != 3 || !int.TryParse(parts[1], out m) || !int.TryParse(parts[2], out d)) return iso;
            return String.Format("{0}.{1:00}", m, d);
        }
    }

    // MARK: - Settings (%APPDATA%\UsageBar\settings.json — mirrors the mac defaults keys)

    class Settings
    {
        public string Mode = "agent";            // agent | model
        public string VisibleAgents = "";        // csv; empty = 总量 only
        public string VisibleModels = "";
        public string Colors = "";               // name=#RRGGBB;...
        public string AgentOrder = "";
        public string ModelOrder = "";
        public bool FirstRunDone = false;
        public List<RemoteConnection> RemoteConnections = new List<RemoteConnection>();

        public static string DirectoryPath()
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageBar");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        static string Path()
        {
            return System.IO.Path.Combine(DirectoryPath(), "settings.json");
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(Path()))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    Settings s = js.Deserialize<Settings>(File.ReadAllText(Path(), Encoding.UTF8));
                    if (s != null) return s;
                }
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                File.WriteAllText(Path(), js.Serialize(this), Encoding.UTF8);
            }
            catch { }
        }

        public string VisibleFor(string mode) { return mode == "model" ? VisibleModels : VisibleAgents; }

        public void SetVisibleFor(string mode, string csv)
        {
            if (mode == "model") VisibleModels = csv; else VisibleAgents = csv;
        }

        public string OrderFor(string mode) { return mode == "model" ? ModelOrder : AgentOrder; }

        public void SetOrderFor(string mode, string csv)
        {
            if (mode == "model") ModelOrder = csv; else AgentOrder = csv;
        }
    }

    // MARK: - Data model + ccusage engine

    class DayPoint
    {
        public string Date;
        public Dictionary<string, long> Agents;
        public Dictionary<string, long> Models;

        public DayPoint() : this("") { }

        public DayPoint(string date)
        {
            Date = date;
            Agents = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Models = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
    }

    class Payload
    {
        public List<DayPoint> Days;
        public long Total;
    }

    class UpdateItem
    {
        public string Installed;
        public string Latest;

        public UpdateItem(string installed, string latest)
        {
            Installed = installed;
            Latest = latest;
        }
    }

    // update strip states (mirrors the mac UpdateState enum)
    enum UpdateState { Idle = 0, Checking = 1, Updating = 2, Result = 3 }

    class Store
    {
        public string Title = "…";
        public long Total;
        public List<DayPoint> AllDays = new List<DayPoint>();
        public List<DayPoint> Points = new List<DayPoint>();   // chart window (last 7)
        public HashSet<string> KnownModels = new HashSet<string>();
        public List<string> SupportedAgents = new List<string>();

        public bool EngineMissing;
        public bool EngineInstalling;
        public string EngineError;
        public bool HasNpm;

        public UpdateState Update = UpdateState.Idle;
        public UpdateItem EngineUpdate;
        public UpdateItem AppUpdate;
        public string UpdateError;
    }

    static class Ccusage
    {
        public class RunResult
        {
            public int Code;
            public string Out;
            public string Err;
        }

        // run through cmd so npm's ccusage.cmd shim resolves from PATH
        public static RunResult Run(string command, int timeoutMs)
        {
            return RunProcess("cmd.exe", "/c " + command, timeoutMs);
        }

        public static RunResult RunProcess(string file, string arguments, int timeoutMs)
        {
            RunResult r = new RunResult();
            r.Code = -1;
            r.Out = "";
            r.Err = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = file;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    string stdout = null, stderr = null;
                    Thread outT = new Thread(delegate() { stdout = p.StandardOutput.ReadToEnd(); });
                    Thread errT = new Thread(delegate() { stderr = p.StandardError.ReadToEnd(); });
                    outT.IsBackground = true; errT.IsBackground = true;
                    outT.Start(); errT.Start();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        r.Err = "超时";
                        outT.Join(1000); errT.Join(1000);
                        r.Out = stdout ?? "";
                        return r;
                    }
                    outT.Join(5000); errT.Join(5000);
                    r.Out = stdout ?? "";
                    r.Err = stderr ?? "";
                    r.Code = p.ExitCode;
                }
            }
            catch (Exception ex) { r.Err = ex.Message; }
            return r;
        }

        public static bool Installed()
        {
            return Run("ccusage --version", 15000).Code == 0;
        }

        // ccusage --help lists every agent CLI it can parse; each such line ends with
        // "usage commands". Parsed live so the 支持、无数据 section follows the
        // installed ccusage (same trick as the mac app).
        public static List<string> ParseAgents()
        {
            RunResult r = Run("ccusage --help", 20000);
            List<string> agents = new List<string>();
            if (r.Code != 0) return agents;
            string[] lines = r.Out.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (!line.Contains("usage commands")) continue;
                string first = line.Trim().Split(new[] { ' ', '\t' })[0];
                if (first.Length > 0 && !agents.Contains(first)) agents.Add(first);
            }
            return agents;
        }

        static Dictionary<string, object> AsDict(object o) { return o as Dictionary<string, object>; }

        static object[] AsArr(object o)
        {
            if (o is object[]) return (object[])o;
            List<object> list = o as List<object>;               // some serializers yield List<object>
            if (list != null) return list.ToArray();
            System.Collections.ArrayList al = o as System.Collections.ArrayList;
            if (al != null) return al.ToArray();
            return null;
        }
        static string AsStr(object o) { return o as string; }

        static long AsLong(object o)
        {
            if (o == null) return 0;
            if (!(o is int || o is long || o is decimal || o is double)) throw new FormatException("Token 必须为数字");
            decimal number = Convert.ToDecimal(o);
            if (number != Decimal.Truncate(number)) throw new FormatException("Token 必须为整数");
            long value = Convert.ToInt64(o);
            if (value < 0) throw new FormatException("Token 不能为负数");
            return value;
        }

        static void AddTo(Dictionary<string, long> dict, string key, long v)
        {
            if (!dict.ContainsKey(key)) dict[key] = 0;
            dict[key] = checked(dict[key] + v);
        }

        // Full-history fetch (3650 days) like the mac app: the chart slices the last
        // 7 days, model discovery covers everything ccusage can find. ponytail: one
        // big JSON call per refresh; if that ever hurts, switch to a shorter window.
        public static Payload RunJSON()
        {
            DateTime today = UsageMerge.Today;
            DateTime start = today.AddDays(-3649);
            string since = start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            RunResult r = Run("ccusage daily --since " + since + " --until " + UsageMerge.DateKey(today)
                + " --timezone Asia/Shanghai --offline --json --by-agent", 120000);
            if (r.Code != 0)
            {
                Program.Log("RunJSON: ccusage exit=" + r.Code + " err=" + (r.Err ?? "").Trim());
                return null;
            }

            try { return ParseJSON(r.Out, today, true); }
            catch (Exception ex) { Program.Log("RunJSON: " + ex.GetType().Name); return null; }
        }

        public static Payload ParseJSON(string json, DateTime today, bool localLedger)
        {
            DateTime start = today.AddDays(-3649);
            string since = UsageMerge.DateKey(start);
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root;
            try { root = js.Deserialize<Dictionary<string, object>>(json); }
            catch (Exception ex)
            {
                throw new FormatException("用量 JSON 无效", ex);
            }
            if (root == null)
            {
                throw new FormatException("用量 JSON 为空");
            }

            Dictionary<string, Dictionary<string, long>> agentsByDay = new Dictionary<string, Dictionary<string, long>>();
            Dictionary<string, Dictionary<string, long>> modelsByDay = new Dictionary<string, Dictionary<string, long>>();

            object[] daily = AsArr(root.ContainsKey("daily") ? root["daily"] : null);
            if (daily == null) throw new FormatException("缺少 daily 日报");
            if (daily != null)
            {
                foreach (object dayObj in daily)
                {
                    Dictionary<string, object> day = AsDict(dayObj);
                    if (day == null) throw new FormatException("日报结构无效");
                    string key = day.ContainsKey("period") ? (AsStr(day["period"]) ?? AsStr(day.ContainsKey("date") ? day["date"] : null)) : AsStr(day.ContainsKey("date") ? day["date"] : null);
                    DateTime date;
                    if (!DateTime.TryParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out date)) throw new FormatException("日报日期无效");
                    if (date < start || date > today) continue;
                    Dictionary<string, long> ag;
                    Dictionary<string, long> md;
                    if (!agentsByDay.TryGetValue(key, out ag)) { ag = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); agentsByDay[key] = ag; }
                    if (!modelsByDay.TryGetValue(key, out md)) { md = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); modelsByDay[key] = md; }
                    object[] agentArr = AsArr(day.ContainsKey("agents") ? day["agents"] : null);
                    if (agentArr == null) throw new FormatException("缺少工具分类，请使用 --by-agent");
                    foreach (object agentObj in agentArr)
                    {
                        Dictionary<string, object> a = AsDict(agentObj);
                        if (a == null) throw new FormatException("工具分类结构无效");
                        string agentName = AsStr(a.ContainsKey("agent") ? a["agent"] : null);
                        if (String.IsNullOrWhiteSpace(agentName)) throw new FormatException("工具名称无效");
                        if (!a.ContainsKey("totalTokens") || a["totalTokens"] == null) throw new FormatException("缺少工具 Token 总数");
                        AddTo(ag, agentName, AsLong(a["totalTokens"]));
                        object[] mbArr = AsArr(a.ContainsKey("modelBreakdowns") ? a["modelBreakdowns"] : null);
                        if (mbArr == null) continue;
                        foreach (object mbObj in mbArr)
                        {
                            Dictionary<string, object> m = AsDict(mbObj);
                            if (m == null) throw new FormatException("模型分类结构无效");
                            string modelName = AsStr(m.ContainsKey("modelName") ? m["modelName"] : null);
                            if (String.IsNullOrWhiteSpace(modelName)) throw new FormatException("模型名称无效");
                            long t = checked(AsLong(m.ContainsKey("inputTokens") ? m["inputTokens"] : null)
                                   + AsLong(m.ContainsKey("outputTokens") ? m["outputTokens"] : null)
                                   + AsLong(m.ContainsKey("cacheCreationTokens") ? m["cacheCreationTokens"] : null)
                                   + AsLong(m.ContainsKey("cacheReadTokens") ? m["cacheReadTokens"] : null));
                            AddTo(md, modelName, t);
                        }
                    }
                }
            }

            // optional deepseek ledger, merged like the mac app does
            try
            {
                string dshPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", ".dshw-usage.json");
                if (localLedger && File.Exists(dshPath))
                {
                    Dictionary<string, object> ledger = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(dshPath, Encoding.UTF8));
                    object[] events = ledger != null ? AsArr(ledger.ContainsKey("events") ? ledger["events"] : null) : null;
                    if (events != null)
                    {
                        foreach (object evObj in events)
                        {
                            Dictionary<string, object> e = AsDict(evObj);
                            if (e == null) continue;
                            string day = AsStr(e.ContainsKey("day") ? e["day"] : null);
                            if (day == null || String.Compare(day, since, StringComparison.Ordinal) < 0) continue;
                            if (!agentsByDay.ContainsKey(day)) agentsByDay[day] = new Dictionary<string, long>();
                            if (!modelsByDay.ContainsKey(day)) modelsByDay[day] = new Dictionary<string, long>();
                            AddTo(agentsByDay[day], "dsh", AsLong(e.ContainsKey("tokens") ? e["tokens"] : null));
                            AddTo(modelsByDay[day], AsStr(e.ContainsKey("model") ? e["model"] : null) ?? "deepseek", AsLong(e.ContainsKey("tokens") ? e["tokens"] : null));
                        }
                    }
                }
            }
            catch { }

            List<DayPoint> days = new List<DayPoint>();
            for (DateTime cur = start; cur <= today; cur = cur.AddDays(1))
            {
                string k = UsageMerge.DateKey(cur);
                DayPoint dp = new DayPoint(k);
                Dictionary<string, long> ag;
                Dictionary<string, long> md;
                if (agentsByDay.TryGetValue(k, out ag)) dp.Agents = ag;
                if (modelsByDay.TryGetValue(k, out md)) dp.Models = md;
                days.Add(dp);
            }
            long total = 0;
            if (days.Count > 0) total = days[days.Count - 1].Agents.Values.Sum();
            return new Payload { Days = days, Total = total };
        }
    }

    // MARK: - Version helpers (same rules as mac)

    static class Ver
    {
        // "ccusage 20.0.26" / "v20.0.26" → "20.0.26"
        public static string VersionToken(string s)
        {
            if (s == null) return null;
            string[] parts = s.Split(new[] { ' ', '\n', '\r', '\t', 'v' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string t in parts)
            {
                if (t.Contains(".") && t.Split('.').Length >= 2) return t;
            }
            return null;
        }

        // a > b, dotted numerics
        public static bool Newer(string a, string b)
        {
            string[] pa = a.Split('.');
            string[] pb = b.Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? ToInt(pa[i]) : 0;
                int y = i < pb.Length ? ToInt(pb[i]) : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        static int ToInt(string s)
        {
            int v;
            if (!int.TryParse(s, out v)) return 0;
            return v;
        }
    }

    // MARK: - View logic (series/visible/colors — ported from the SwiftUI view)

    static class ViewLogic
    {
        static readonly string[] AgentOrderKnown = { "总量", "zcode", "codex", "dsh" };
        static readonly string[] ModelOrderKnown = { "总量", "GLM-5.3-Flash", "GLM-5.3", "deepseek-flash" };

        static readonly Dictionary<string, Color> AgentColor = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "zcode", Color.FromArgb(0, 122, 255) },
            { "codex", Color.FromArgb(175, 82, 222) },
            { "dsh", Color.FromArgb(255, 149, 0) }
        };

        public static Color AgentFallbackColor(string name)
        {
            string l = name.ToLowerInvariant();
            if (l.Contains("claude")) return Color.FromArgb(255, 59, 48);
            if (l.Contains("gemini")) return Color.FromArgb(64, 200, 224);
            if (l.Contains("grok")) return Color.FromArgb(255, 45, 85);
            if (l.Contains("droid")) return Color.FromArgb(162, 132, 94);
            if (l.Contains("opencode")) return Color.FromArgb(88, 86, 214);
            if (l.Contains("cursor")) return Color.FromArgb(85, 190, 240);
            return Color.FromArgb(142, 142, 147);
        }

        public static Color ModelColor(string name)
        {
            string l = name.ToLowerInvariant();
            if (l.StartsWith("glm")) return Color.FromArgb(0, 122, 255);
            if (l.Contains("deepseek")) return Color.FromArgb(255, 149, 0);
            if (l.StartsWith("gpt")) return Color.FromArgb(175, 82, 222);
            if (l.Contains("claude")) return Color.FromArgb(255, 59, 48);
            if (l.Contains("gemini")) return Color.FromArgb(64, 200, 224);
            if (l.Contains("kimi")) return Color.FromArgb(255, 45, 85);
            return Color.FromArgb(142, 142, 147);
        }

        public static Dictionary<string, Color> ParseColorOverrides(string stored)
        {
            Dictionary<string, Color> map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrEmpty(stored)) return map;
            foreach (string part in stored.Split(';'))
            {
                string[] kv = part.Split('=');
                if (kv.Length != 2) continue;
                try
                {
                    string hex = kv[1].TrimStart('#');
                    if (hex.Length != 6) continue;
                    map[kv[0]] = Color.FromArgb(
                        Convert.ToInt32(hex.Substring(0, 2), 16),
                        Convert.ToInt32(hex.Substring(2, 2), 16),
                        Convert.ToInt32(hex.Substring(4, 2), 16));
                }
                catch { }
            }
            return map;
        }

        public static string Mode(Settings s) { return s.Mode; }

        public static HashSet<string> Discovered(Store store, string mode)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mode == "model")
            {
                foreach (string m in store.KnownModels) names.Add(m);  // full window
            }
            else
            {
                foreach (DayPoint p in store.Points)
                    foreach (string a in p.Agents.Keys) names.Add(a);
                names.Add("zcode");
                names.Add("codex");
                names.Add("dsh");
            }
            names.Add("总量");
            return names;
        }

        public static long Value(DayPoint p, string name, string mode)
        {
            if (p == null) return 0;
            Dictionary<string, long> src = mode == "model" ? p.Models : p.Agents;
            if (name == "总量")
            {
                long sum = 0;
                foreach (long v in src.Values) sum += v;
                return sum;
            }
            long outv;
            if (src.TryGetValue(name, out outv)) return outv;
            return 0;
        }

        public static Dictionary<string, long> WindowTotals(Store store, HashSet<string> discovered, string mode)
        {
            Dictionary<string, long> t = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (DayPoint p in store.Points)
            {
                foreach (string n in discovered)
                {
                    if (!t.ContainsKey(n)) t[n] = 0;
                    t[n] += Value(p, n, mode);
                }
            }
            return t;
        }

        // nothing auto-selected except 总量 — series appear when checked in the menu
        public static HashSet<string> VisibleSet(Settings settings, string mode)
        {
            string stored = settings.VisibleFor(mode);
            if (String.IsNullOrEmpty(stored))
            {
                HashSet<string> def = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                def.Add("总量");
                return def;
            }
            return new HashSet<string>(stored.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        }

        public static void SetVisible(Settings settings, string mode, HashSet<string> names)
        {
            settings.SetVisibleFor(mode, String.Join(",", names.ToArray()));
            settings.Save();
        }

        public static List<string> Series(Settings settings, Store store, string mode)
        {
            HashSet<string> visible = VisibleSet(settings, mode);
            HashSet<string> discovered = Discovered(store, mode);
            List<string> names = new List<string>();
            foreach (string n in discovered)
                if (visible.Contains(n)) names.Add(n);

            string[] known = mode == "agent" ? AgentOrderKnown : ModelOrderKnown;
            List<string> ordered = new List<string>();
            foreach (string k in known)
            {
                string match = names.Find(n => String.Equals(n, k, StringComparison.OrdinalIgnoreCase));
                if (match != null) ordered.Add(match);
            }
            List<string> rest = new List<string>();
            foreach (string n in names)
                if (!known.Any(k => String.Equals(k, n, StringComparison.OrdinalIgnoreCase))) rest.Add(n);
            rest.Sort(StringComparer.Ordinal);
            ordered.AddRange(rest);
            if (ordered.Count == 0) ordered.Add("总量");
            return ordered;
        }

        // the list mirrors the chart selection; a user-dragged order wins, the rest
        // follow 总量 first then 7-day usage (same as mac listSeries)
        public static List<string> ListSeries(Settings settings, Store store, string mode)
        {
            List<string> series = Series(settings, store, mode);
            Dictionary<string, long> totals = WindowTotals(store, Discovered(store, mode), mode);
            List<string> saved = new List<string>();
            string savedCsv = settings.OrderFor(mode);
            if (!String.IsNullOrEmpty(savedCsv))
            {
                foreach (string n in savedCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string match = series.Find(s => String.Equals(s, n, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !saved.Contains(match)) saved.Add(match);
                }
            }
            List<string> rest = new List<string>();
            foreach (string n in series)
                if (!saved.Contains(n)) rest.Add(n);
            rest.Sort(delegate(string a, string b)
            {
                bool aTotal = a == "总量";
                bool bTotal = b == "总量";
                if (aTotal != bTotal) return aTotal ? -1 : 1;
                long ta, tb;
                totals.TryGetValue(a, out ta);
                totals.TryGetValue(b, out tb);
                return tb.CompareTo(ta);
            });
            saved.AddRange(rest);
            return saved;
        }

        public static Color ColorFor(Settings settings, string name, string mode, bool forChart)
        {
            Dictionary<string, Color> overrides = ParseColorOverrides(settings.Colors);
            Color c;
            if (overrides.TryGetValue(name, out c)) return c;
            if (name == "总量") return Color.FromArgb(40, 40, 45);  // "primary"
            if (mode == "agent")
            {
                if (AgentColor.ContainsKey(name)) return AgentColor[name];
                return AgentFallbackColor(name);
            }
            return ModelColor(name);
        }
    }

    // MARK: - Win32 interop (only what the message window and tray icon need)

    static class Win32
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static WndProcDelegate defProc = new WndProcDelegate(DefWindowProcW);  // keep alive
        public static IntPtr DefProcPtr = Marshal.GetFunctionPointerForDelegate(defProc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassW(ref WNDCLASS wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
            uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessageW(string message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public const uint WS_EX_TOOLWINDOW = 0x80;
    }

    // MARK: - Bolt glyph (drawn, not a font glyph — no missing-character risk)

    static class Bolt
    {
        static readonly PointF[] Unit = new PointF[]
        {
            new PointF(0.62f, 0.00f), new PointF(0.18f, 0.56f), new PointF(0.45f, 0.56f),
            new PointF(0.38f, 1.00f), new PointF(0.82f, 0.44f), new PointF(0.55f, 0.44f)
        };

        public static void Draw(Graphics g, RectangleF r, Color fill, Color outline)
        {
            PointF[] pts = new PointF[Unit.Length];
            for (int i = 0; i < Unit.Length; i++)
            {
                pts[i] = new PointF(r.Left + Unit[i].X * r.Width, r.Top + Unit[i].Y * r.Height);
            }
            using (SolidBrush b = new SolidBrush(fill)) { g.FillPolygon(b, pts); }
            if (outline.A > 0)
            {
                using (Pen p = new Pen(outline, Math.Max(1f, r.Width * 0.05f))) { g.DrawPolygon(p, pts); }
            }
        }

        public static Icon MakeTrayIcon()
        {
            int size = SystemInformation.SmallIconSize.Width;
            if (size < 16) size = 16;
            using (Bitmap bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Draw(g, new RectangleF(1f, 1f, size - 2, size - 2),
                        Color.FromArgb(255, 196, 0), Color.FromArgb(150, 110, 0));
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Win32.DestroyIcon(h);
                return icon;
            }
        }
    }

    // 隐藏消息窗口：资源管理器重启恢复托盘，重复启动唤起面板。

    class MessageWindow : NativeWindow
    {
        const string ClassName = "UsageBarMsgWin";
        static bool classRegistered;
        static uint taskbarCreatedMsg;
        static uint showFlyoutMsg;
        readonly AppContext ctx;

        public MessageWindow(AppContext context)
        {
            ctx = context;
            if (taskbarCreatedMsg == 0)
            {
                taskbarCreatedMsg = Win32.RegisterWindowMessageW("TaskbarCreated");
                showFlyoutMsg = Win32.RegisterWindowMessageW("UsageBar.ShowFlyout");
            }
            if (!classRegistered)
            {
                Win32.WNDCLASS wc = new Win32.WNDCLASS();
                wc.lpfnWndProc = Win32.DefProcPtr;
                wc.hInstance = Marshal.GetHINSTANCE(typeof(Program).Module);
                wc.lpszClassName = ClassName;
                Win32.RegisterClassW(ref wc);
                classRegistered = true;
            }
            // 隐藏顶层工具窗口接收系统广播，消息专用窗口收不到广播。
            IntPtr h = Win32.CreateWindowExW(Win32.WS_EX_TOOLWINDOW, ClassName, "", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, Marshal.GetHINSTANCE(typeof(Program).Module), IntPtr.Zero);
            if (h != IntPtr.Zero) AssignHandle(h);
        }

        protected override void WndProc(ref Message m)
        {
            if (taskbarCreatedMsg != 0 && m.Msg == (int)taskbarCreatedMsg)
            {
                ctx.OnTaskbarCreated();
            }
            else if (showFlyoutMsg != 0 && m.Msg == (int)showFlyoutMsg)
            {
                ctx.ShowFlyout();
            }
            base.WndProc(ref m);
        }
    }

    // MARK: - Chart (7-day smoothed lines — the GDI+ twin of the mac Swift Charts view)

    class ChartPanel : Control
    {
        public List<DayPoint> Points = new List<DayPoint>();
        public List<string> SeriesNames = new List<string>();
        public Func<string, Color> ColorFor;
        public Action<int> OnHover;
        public int HoverIndex = -1;

        public ChartPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Program.UiFont(10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            int n = Points.Count;
            if (n == 0)
            {
                // first refresh still in flight — placeholder instead of blank
                using (Font font = Program.UiFont(12))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(160, 130, 130, 135)))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("加载中…", font, b, new RectangleF(0, 0, Width, Height), sf);
                }
                return;
            }

            int padX = Program.Px(10);
            int labelH = Math.Max(Program.Px(14), Font.Height);
            int plotW = Math.Max(Width - padX * 2, 1);
            int plotH = Math.Max(Height - labelH - Program.Px(4), 1);
            // 顶部留白随画幅和 DPI 调整，避免峰值与线宽贴边被裁切。
            float plotTop = Math.Max(Program.Px(8), plotH * 0.06f);
            float plotBottom = plotH - Program.Px(2);
            float plotRange = Math.Max(plotBottom - plotTop, 1f);
            float step = n > 1 ? (float)plotW / (n - 1) : 0;
            Func<int, float> xOf = delegate(int i) { return padX + i * step; };

            // 所有已选系列共用坐标轴，上限包含总量，避免隐藏主系列后总量被压在顶边。
            long yMax = 1;
            foreach (string name in SeriesNames)
                foreach (DayPoint p in Points)
                {
                    long v = ViewLogic.Value(p, name, CurrentMode);
                    if (v > yMax) yMax = v;
                }

            // 量级差距较大时共用幂次比例尺，提高小系列可读性；精确用量以列表为准。
            // ponytail: 幂次下限为 0.25，极小系列可能低于 20% 画高；需要显示时再放宽下限。
            double pow = 1.0;
            {
                long minPeak = 0;
                foreach (string name in SeriesNames)
                {
                    if (name == "总量") continue;
                    long peak = 0;
                    foreach (DayPoint pt in Points)
                    {
                        long v = ViewLogic.Value(pt, name, CurrentMode);
                        if (v > peak) peak = v;
                    }
                    if (peak > 0 && (minPeak == 0 || peak < minPeak)) minPeak = peak;
                }
                if (minPeak > 0 && yMax > minPeak)
                {
                    double r = (double)minPeak / yMax;
                    if (r < 0.2) pow = Math.Max(0.25, Math.Log(0.2) / Math.Log(r));
                }
            }
            Func<long, float> yOf = delegate(long v)
            {
                double frac = v <= 0 ? 0.0 : Math.Pow((double)v / yMax, pow);
                return plotBottom - (float)frac * plotRange;
            };

            // per-day dashed gridlines
            using (Pen grid = new Pen(Color.FromArgb(70, 150, 150, 155), 1f))
            {
                grid.DashPattern = new float[] { 4f, 3f };
                for (int i = 0; i < n; i++)
                {
                    float x = xOf(i);
                    g.DrawLine(grid, x, 1, x, plotH);
                }
            }

            // 总量先画成半透明虚线，避免遮住与之重合的工具曲线。
            if (SeriesNames.Contains("总量"))
            {
                Color c = ColorFor != null ? ColorFor("总量") : Color.Gray;
                PointF[] pts = new PointF[n];
                for (int i = 0; i < n; i++) pts[i] = new PointF(xOf(i), yOf(ViewLogic.Value(Points[i], "总量", CurrentMode)));
                using (GraphicsPath path = MonotonePath(pts))
                using (Pen pen = new Pen(Color.FromArgb(150, c), 1.75f * Program.Scale))
                {
                    pen.DashPattern = new float[] { 5f, 3f };
                    g.DrawPath(pen, path);
                }
            }

            // 各系列独立采用单调三次插值，保持零值段与零轴重合。
            foreach (string name in SeriesNames)
            {
                if (name == "总量") continue;
                Color c = ColorFor != null ? ColorFor(name) : Color.Gray;
                PointF[] pts = new PointF[n];
                for (int i = 0; i < n; i++) pts[i] = new PointF(xOf(i), yOf(ViewLogic.Value(Points[i], name, CurrentMode)));
                using (GraphicsPath path = MonotonePath(pts))
                using (Pen pen = new Pen(c, 2.5f * Program.Scale))
                    g.DrawPath(pen, path);
            }

            // hover rule
            if (HoverIndex >= 0 && HoverIndex < n)
            {
                using (Pen rule = new Pen(Color.FromArgb(110, 90, 90, 95), 1.5f))
                {
                    float x = xOf(HoverIndex);
                    g.DrawLine(rule, x, 1, x, plotH);
                }
            }

            // x labels
            using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 120, 120, 125)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                float lastRight = -Program.Px(4);
                float lastWidth = g.MeasureString(Fmt.CnDate(Points[n - 1].Date), Font).Width;
                for (int i = 0; i < n; i++)
                {
                    string label = Fmt.CnDate(Points[i].Date);
                    float labelW = g.MeasureString(label, Font).Width;
                    float x = Math.Max(labelW / 2, Math.Min(Width - labelW / 2, xOf(i)));
                    // 窄画幅优先保留首尾日期，省略会重叠的中间刻度。
                    if (i > 0 && i < n - 1 && (x - labelW / 2 < lastRight + Program.Px(4)
                        || x + labelW / 2 + Program.Px(4) > Width - lastWidth)) continue;
                    g.DrawString(label, Font, b, x, plotH + Program.Px(3), sf);
                    lastRight = x + labelW / 2;
                }
            }
        }

        string CurrentMode { get { return AppCtx != null ? AppCtx.Settings.Mode : "agent"; } }
        public AppContext AppCtx;

        // 使用 Fritsch–Carlson 单调三次插值构造 Bézier 路径，曲线保持在相邻端点之间。
        static GraphicsPath MonotonePath(PointF[] pts)
        {
            GraphicsPath path = new GraphicsPath();
            int n = pts.Length;
            if (n < 2) { if (n == 1) path.AddLine(pts[0], pts[0]); return path; }
            float[] d = new float[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                float dx = pts[i + 1].X - pts[i].X;
                d[i] = dx != 0 ? (pts[i + 1].Y - pts[i].Y) / dx : 0;
            }
            float[] m = new float[n];
            m[0] = d[0];
            m[n - 1] = d[n - 2];
            // 峰谷处切线归零，防止平滑曲线超过相邻数据点。
            for (int i = 1; i < n - 1; i++)
                m[i] = d[i - 1] * d[i] <= 0 ? 0 : 0.5f * (d[i - 1] + d[i]);
            for (int i = 0; i < n - 1; i++)
            {
                if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
                float t0 = m[i] / d[i], t1 = m[i + 1] / d[i];
                float t2 = t0 * t0 + t1 * t1;
                if (t2 > 9f)
                {
                    float s = 3f / (float)Math.Sqrt(t2);
                    m[i] = s * t0 * d[i];
                    m[i + 1] = s * t1 * d[i];
                }
            }
            for (int i = 0; i < n - 1; i++)
            {
                float h = pts[i + 1].X - pts[i].X;
                PointF c1 = new PointF(pts[i].X + h / 3f, pts[i].Y + m[i] * h / 3f);
                PointF c2 = new PointF(pts[i + 1].X - h / 3f, pts[i + 1].Y - m[i + 1] * h / 3f);
                path.AddBezier(pts[i], c1, c2, pts[i + 1]);
            }
            return path;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = Points.Count;
            if (n == 0) return;
            int padX = Program.Px(10);
            int plotW = Math.Max(Width - padX * 2, 1);
            float step = n > 1 ? (float)plotW / (n - 1) : 0;
            int idx = step > 0 ? (int)Math.Round((e.X - padX) / step) : 0;
            idx = Math.Max(0, Math.Min(n - 1, idx));
            if (idx != HoverIndex)
            {
                HoverIndex = idx;
                Invalidate();
                if (OnHover != null) OnHover(idx);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (HoverIndex != -1)
            {
                HoverIndex = -1;
                Invalidate();
                if (OnHover != null) OnHover(-1);
            }
        }
    }

    // MARK: - Series list (color dot + name + today + 7d, hover-follow, drag reorder)

    class SeriesListPanel : Control
    {
        public class Row
        {
            public string Name;
            public long Today;
            public long Week;
            public Color Color;
        }

        public List<Row> Rows = new List<Row>();
        public DayPoint DisplayPoint;
        public Action<string> OnColorClick;
        public Action<string, string> OnReorder;
        public int HoverIndex = -1;
        int dragIndex = -1;

        public SeriesListPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public int RowHeight { get { return Program.Px(21); } }
        public int HeaderHeight { get { return Program.Px(20); } }
        public int AutoHeight
        {
            get
            {
                int h = Math.Min(Rows.Count * RowHeight + HeaderHeight + Program.Px(10), Program.Px(200));
                return Math.Max(h, Program.Px(40));
            }
        }

        int RowAt(int y) { return (y - HeaderHeight) / RowHeight; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            if (DisplayPoint == null || Rows.Count == 0) return;

            int pad = Program.Px(6);
            int dotCx = Program.Px(16);
            int nameX = Program.Px(26);
            int nameW = Program.Px(108);
            int right = Width - pad;
            int weekW = Program.Px(92);
            int weekRight = right;
            int weekLeft = right - weekW;
            int todayRight = weekLeft - Program.Px(4);
            int todayW = Program.Px(84);
            int todayLeft = todayRight - todayW;

            using (Font font = Program.UiFont(11))
            using (Font headerFont = Program.UiFont(10))
            {
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(140, 110, 110, 115)))
                {
                    TextRenderer.DrawText(g, Fmt.CnDate(DisplayPoint.Date) + " 用量", headerFont,
                        new Rectangle(nameX, 0, nameW + Program.Px(60), HeaderHeight),
                        Color.FromArgb(140, 110, 110, 115),
                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                    TextRenderer.DrawText(g, "7天", headerFont,
                        new Rectangle(weekLeft - Program.Px(20), 0, Program.Px(20) + weekW, HeaderHeight),
                        Color.FromArgb(170, 120, 120, 125),
                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                }

                for (int i = 0; i < Rows.Count; i++)
                {
                    Row row = Rows[i];
                    int y = HeaderHeight + i * RowHeight;
                    Rectangle rowRect = new Rectangle(pad, y, Width - pad * 2, RowHeight);

                    if (i == HoverIndex && dragIndex == -1)
                    {
                        using (GraphicsPath path = RoundedRect(rowRect, Program.Px(5)))
                        using (SolidBrush hb = new SolidBrush(Color.FromArgb(242, 242, 243)))
                            g.FillPath(hb, path);
                    }
                    float alpha = dragIndex == i ? 0.35f : 1f;
                    Color textColor = row.Name == "总量"
                        ? Color.FromArgb(40, 40, 45)
                        : Color.FromArgb(90, 80, 80, 85);

                    using (SolidBrush db = new SolidBrush(row.Color))
                        g.FillEllipse(db, dotCx - Program.Px(4), y + RowHeight / 2 - Program.Px(4), Program.Px(8), Program.Px(8));

                    TextRenderer.DrawText(g, FitName(g, font, row.Name, nameW), font,
                        new Rectangle(nameX, y, nameW, RowHeight), textColor,
                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, Fmt.Human(row.Today), font,
                        new Rectangle(todayLeft, y, todayW, RowHeight), Color.FromArgb(40, 40, 45),
                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                    TextRenderer.DrawText(g, Fmt.Human(row.Week), font,
                        new Rectangle(weekLeft, y, weekW, RowHeight), textColor,
                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                }
            }
        }

        string FitName(Graphics g, Font font, string text, int maxW)
        {
            if (TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding).Width <= maxW) return text;
            while (text.Length > 1 && TextRenderer.MeasureText(g, text + "…", font, Size.Empty, TextFormatFlags.NoPadding).Width > maxW)
                text = text.Substring(0, text.Length - 1);
            return text + "…";
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = RowAt(e.Y);
            bool valid = idx >= 0 && idx < Rows.Count;
            if (dragIndex >= 0 && valid)
            {
                if (idx != dragIndex && OnReorder != null)
                {
                    string dragged = Rows[dragIndex].Name;
                    string target = Rows[idx].Name;
                    // live hover-swap like the mac drop delegate
                    Row moved = Rows[dragIndex];
                    Rows.RemoveAt(dragIndex);
                    Rows.Insert(idx, moved);
                    dragIndex = idx;
                    Invalidate();
                    OnReorder(dragged, target);
                }
            }
            else
            {
                int newHover = valid ? idx : -1;
                if (newHover != HoverIndex)
                {
                    HoverIndex = newHover;
                    Invalidate();
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (HoverIndex != -1) { HoverIndex = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int idx = RowAt(e.Y);
            if (idx < 0 || idx >= Rows.Count) return;
            if (e.X < Program.Px(26))
            {
                if (OnColorClick != null) OnColorClick(Rows[idx].Name);
            }
            else
            {
                dragIndex = idx;
                Capture = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragIndex != -1)
            {
                dragIndex = -1;
                Capture = false;
                Invalidate();
            }
        }
    }

    // MARK: - Flyout (the popover: mode toggle + series menu + chart + list + actions)

    class FlyoutForm : Form
    {
        readonly AppContext ctx;

        Panel mainContent;
        Label segAgent, segModel;
        Button seriesBtn;
        ContextMenuStrip seriesMenu;
        ChartPanel chart;
        SeriesListPanel list;
        Panel divider;

        Panel engineCard;
        Label engineTitle, engineDesc, engineStatus;
        Button engineNpmBtn;

        Panel updateStrip;
        FlowLayoutPanel updateFlow;

        Panel bottomBar;
        Button dailyBtn, refreshBtn, checkBtn, quitBtn, remoteBtn;
        CheckBox autostartCheck;

        public System.Windows.Forms.Timer dismissTimer;

        public DateTime LastDeactivateClose;

        public FlyoutForm(AppContext context)
        {
            ctx = context;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            KeyPreview = true;
            Text = "UsageBar";

            int w = Program.Px(380);

            mainContent = new Panel();
            mainContent.BackColor = Color.White;

            // 按工具 / 按模型 segmented + series menu button
            segAgent = MakeSegment("按工具");
            segModel = MakeSegment("按模型");
            segAgent.Click += delegate { ctx.SwitchMode("agent"); };
            segModel.Click += delegate { ctx.SwitchMode("model"); };

            seriesBtn = new Button();
            seriesBtn.Text = "系列 ▼";
            seriesBtn.Font = Program.UiFont(12);
            seriesBtn.FlatStyle = FlatStyle.Flat;
            seriesBtn.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 214);
            seriesBtn.Size = new Size(Program.Px(70), Program.Px(26));
            seriesBtn.Cursor = Cursors.Hand;
            seriesBtn.Click += delegate { seriesMenu.Show(seriesBtn, new Point(0, seriesBtn.Height)); };

            seriesMenu = new ContextMenuStrip();
            seriesMenu.ShowImageMargin = false;
            seriesMenu.Opening += delegate { BuildSeriesMenu(); };

            chart = new ChartPanel();
            chart.AppCtx = ctx;
            chart.OnHover = delegate(int idx) { SyncListTo(idx); };
            chart.ColorFor = delegate(string name) { return ViewLogic.ColorFor(ctx.Settings, name, ctx.Settings.Mode, true); };

            list = new SeriesListPanel();
            list.OnColorClick = delegate(string name) { PickColor(name); };
            list.OnReorder = delegate(string dragged, string target) { ctx.ReorderSeries(dragged, target); };

            divider = new Panel();
            divider.BackColor = Color.FromArgb(232, 232, 235);
            divider.Height = 1;

            mainContent.Controls.Add(segAgent);
            mainContent.Controls.Add(segModel);
            mainContent.Controls.Add(seriesBtn);
            mainContent.Controls.Add(chart);
            mainContent.Controls.Add(list);
            mainContent.Controls.Add(divider);
            Controls.Add(mainContent);

            // engine setup card (ccusage missing)
            engineCard = new Panel();
            engineCard.BackColor = Color.FromArgb(246, 246, 247);
            engineTitle = new Label();
            engineTitle.Text = "数据引擎 ccusage 未安装";
            engineTitle.Font = Program.UiFont(13);
            engineTitle.ForeColor = Color.FromArgb(40, 40, 45);
            engineTitle.AutoSize = true;
            engineDesc = new Label();
            engineDesc.Text = "它负责读取各 AI CLI 的本地日志（完全本地，不联网上传）。装好后任务栏即显示用量。";
            engineDesc.Font = Program.UiFont(11);
            engineDesc.ForeColor = Color.FromArgb(120, 120, 125);
            engineDesc.MaximumSize = new Size(Program.Px(300), 0);
            engineStatus = new Label();
            engineStatus.Font = Program.UiFont(11);
            engineStatus.ForeColor = Color.FromArgb(180, 60, 40);
            engineStatus.AutoSize = true;
            engineNpmBtn = new Button();
            engineNpmBtn.Text = "用 npm 安装";
            engineNpmBtn.Font = Program.UiFont(12);
            engineNpmBtn.Size = new Size(Program.Px(110), Program.Px(28));
            engineNpmBtn.Cursor = Cursors.Hand;
            engineNpmBtn.Click += delegate { ctx.InstallEngine(); };
            engineCard.Controls.Add(engineTitle);
            engineCard.Controls.Add(engineDesc);
            engineCard.Controls.Add(engineStatus);
            engineCard.Controls.Add(engineNpmBtn);
            Controls.Add(engineCard);

            // update strip
            updateStrip = new Panel();
            updateStrip.BackColor = Color.White;
            updateFlow = new FlowLayoutPanel();
            updateFlow.FlowDirection = FlowDirection.TopDown;
            updateFlow.AutoSize = true;
            updateFlow.WrapContents = false;
            updateStrip.Controls.Add(updateFlow);
            Controls.Add(updateStrip);

            // bottom bar
            bottomBar = new Panel();
            bottomBar.BackColor = Color.White;
            dailyBtn = MakeBottomButton("终端日报");
            refreshBtn = MakeBottomButton("刷新");
            checkBtn = MakeBottomButton("检查更新");
            quitBtn = MakeBottomButton("退出");
            remoteBtn = MakeBottomButton("远程连接");
            remoteBtn.Click += delegate { ctx.OpenRemoteConnections(); };
            dailyBtn.Click += delegate { ctx.OpenDailyReport(); };
            refreshBtn.Click += delegate { ctx.RefreshData(true); };
            checkBtn.Click += delegate { ctx.CheckForUpdate(); };
            quitBtn.Click += delegate { ctx.Quit(); };
            autostartCheck = new CheckBox();
            autostartCheck.Text = "开机自启";
            autostartCheck.Font = Program.UiFont(11);
            autostartCheck.AutoSize = true;
            autostartCheck.Cursor = Cursors.Hand;
            autostartCheck.CheckedChanged += delegate { ctx.SetAutostart(autostartCheck.Checked); };
            bottomBar.Controls.Add(dailyBtn);
            bottomBar.Controls.Add(refreshBtn);
            bottomBar.Controls.Add(checkBtn);
            bottomBar.Controls.Add(quitBtn);
            bottomBar.Controls.Add(autostartCheck);
            bottomBar.Controls.Add(remoteBtn);
            Controls.Add(bottomBar);

            dismissTimer = new System.Windows.Forms.Timer();
            dismissTimer.Interval = 10000;
            dismissTimer.Tick += delegate
            {
                dismissTimer.Stop();
                if (ctx.Store.Update == UpdateState.Result)
                {
                    ctx.Store.Update = UpdateState.Idle;
                    DataBind();
                }
            };

            Deactivate += delegate
            {
                if (ctx.RemoteDialogOpen) return;
                // USAGEBAR_STICKY=1: test hook — ignore outside-click closing so
                // automated screenshots don't race with real desktop activity
                if (Environment.GetEnvironmentVariable("USAGEBAR_STICKY") == "1") return;
                if (Visible)
                {
                    LastDeactivateClose = DateTime.Now;
                    Hide();
                }
            };

            Size = new Size(w, Program.Px(520));
        }

        Label MakeSegment(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Program.UiFont(12);
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Size = new Size(Program.Px(68), Program.Px(26));
            l.Cursor = Cursors.Hand;
            return l;
        }

        Button MakeBottomButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.Font = Program.UiFont(12);
            b.AutoSize = false;
            b.Size = new Size(TextRenderer.MeasureText(text, Program.UiFont(12)).Width + Program.Px(18), Program.Px(26));
            b.Cursor = Cursors.Hand;
            b.FlatStyle = FlatStyle.System;
            return b;
        }

        void BuildSeriesMenu()
        {
            seriesMenu.Items.Clear();
            string mode = ctx.Settings.Mode;
            Store store = ctx.Store;
            HashSet<string> discovered = ViewLogic.Discovered(store, mode);
            HashSet<string> visible = ViewLogic.VisibleSet(ctx.Settings, mode);
            Dictionary<string, long> totals = ViewLogic.WindowTotals(store, discovered, mode);

            ToolStripMenuItem header = new ToolStripMenuItem(mode == "agent" ? "显示工具" : "显示模型");
            header.Enabled = false;
            seriesMenu.Items.Add(header);

            foreach (string name in discovered.OrderBy(x => x, StringComparer.Ordinal))
            {
                string n = name;
                ToolStripMenuItem item = new ToolStripMenuItem(
                    String.Format("{0}  {1}", n, Fmt.Human(totals.ContainsKey(n) ? totals[n] : 0)));
                item.CheckOnClick = true;
                item.Checked = visible.Contains(n);
                item.CheckedChanged += delegate
                {
                    HashSet<string> v = ViewLogic.VisibleSet(ctx.Settings, mode);
                    if (item.Checked) v.Add(n); else v.Remove(n);
                    ViewLogic.SetVisible(ctx.Settings, mode, v);
                    DataBind();
                };
                seriesMenu.Items.Add(item);
            }

            if (mode == "agent")
            {
                List<string> silent = new List<string>();
                foreach (string a in ctx.Store.SupportedAgents)
                    if (!discovered.Contains(a)) silent.Add(a);
                if (silent.Count > 0)
                {
                    seriesMenu.Items.Add(new ToolStripSeparator());
                    ToolStripMenuItem h2 = new ToolStripMenuItem("支持、无数据（来自 ccusage）");
                    h2.Enabled = false;
                    seriesMenu.Items.Add(h2);
                    foreach (string a in silent)
                    {
                        ToolStripMenuItem it = new ToolStripMenuItem(a);
                        it.Enabled = false;
                        seriesMenu.Items.Add(it);
                    }
                }
            }

            seriesMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem reset = new ToolStripMenuItem("恢复默认（仅显示总量）");
            reset.Click += delegate
            {
                ViewLogic.SetVisible(ctx.Settings, mode, new HashSet<string>());
                ctx.Settings.SetOrderFor(mode, "");
                ctx.Settings.Save();
                DataBind();
            };
            seriesMenu.Items.Add(reset);
        }

        DayPoint PointFor(int idx)
        {
            if (idx >= 0 && idx < ctx.Store.Points.Count) return ctx.Store.Points[idx];
            return ctx.Store.Points.Count > 0 ? ctx.Store.Points[ctx.Store.Points.Count - 1] : null;
        }

        // the list follows the chart hover: whichever day the cursor is on, that
        // day's per-series usage is listed (defaults to today) — same as the mac
        // popover's displayPoint. Rows' values must be recomputed per day.
        void SyncListTo(int idx)
        {
            DayPoint p = PointFor(idx);
            if (p == null) return;
            list.DisplayPoint = p;
            string mode = ctx.Settings.Mode;
            foreach (SeriesListPanel.Row r in list.Rows)
                r.Today = ViewLogic.Value(p, r.Name, mode);
            list.Invalidate();
        }

        void PickColor(string name)
        {
            using (ColorDialog cd = new ColorDialog())
            {
                cd.FullOpen = true;
                Dictionary<string, Color> overrides = ViewLogic.ParseColorOverrides(ctx.Settings.Colors);
                Color cur;
                cd.Color = overrides.TryGetValue(name, out cur)
                    ? cur
                    : ViewLogic.ColorFor(ctx.Settings, name, ctx.Settings.Mode, false);
                if (cd.ShowDialog(this) != DialogResult.OK) return;
                overrides[name] = cd.Color;
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, Color> kv in overrides)
                {
                    parts.Add(String.Format("{0}=#{1:X2}{2:X2}{3:X2}", kv.Key, kv.Value.R, kv.Value.G, kv.Value.B));
                }
                parts.Sort(StringComparer.Ordinal);
                ctx.Settings.Colors = String.Join(";", parts.ToArray());
                ctx.Settings.Save();
                DataBind();
            }
        }

        public void DataBind()
        {
            Store store = ctx.Store;
            string mode = ctx.Settings.Mode;

            segAgent.BackColor = mode == "agent" ? Color.FromArgb(205, 205, 210) : Color.FromArgb(238, 238, 240);
            segModel.BackColor = mode == "model" ? Color.FromArgb(205, 205, 210) : Color.FromArgb(238, 238, 240);

            bool missing = store.EngineMissing;
            mainContent.Visible = !missing;
            engineCard.Visible = missing;

            if (missing)
            {
                engineNpmBtn.Visible = store.HasNpm && !store.EngineInstalling;
                if (store.EngineInstalling)
                {
                    engineStatus.Text = "安装中…首次会连 Node 一起装，可能需要几分钟";
                    engineStatus.ForeColor = Color.FromArgb(120, 120, 125);
                }
                else if (!String.IsNullOrEmpty(store.EngineError))
                {
                    engineStatus.Text = store.EngineError;
                    engineStatus.ForeColor = Color.FromArgb(180, 60, 40);
                }
                else
                {
                    engineStatus.Text = store.HasNpm ? "" : "未检测到 npm：请先安装 Node.js LTS，再手动执行 npm i -g ccusage";
                    engineStatus.ForeColor = Color.FromArgb(120, 120, 125);
                }
            }
            else
            {
                chart.Points = store.Points;
                chart.SeriesNames = ViewLogic.Series(ctx.Settings, store, mode);
                chart.Invalidate();

                DayPoint dp = PointFor(chart.HoverIndex);
                list.DisplayPoint = dp;
                Dictionary<string, long> totals = ViewLogic.WindowTotals(store, ViewLogic.Discovered(store, mode), mode);
                List<SeriesListPanel.Row> rows = new List<SeriesListPanel.Row>();
                foreach (string name in ViewLogic.ListSeries(ctx.Settings, store, mode))
                {
                    SeriesListPanel.Row r = new SeriesListPanel.Row();
                    r.Name = name;
                    r.Today = ViewLogic.Value(dp, name, mode);
                    r.Week = totals.ContainsKey(name) ? totals[name] : 0;
                    r.Color = ViewLogic.ColorFor(ctx.Settings, name, mode, false);
                    rows.Add(r);
                }
                list.Rows = rows;
                list.Height = list.AutoHeight;
                list.Invalidate();
            }

            RebuildUpdateStrip();
            autostartCheck.Checked = AppContext.IsAutostartOn();
            int enabled = ctx.Remotes.States.Count(s => s.Connection.Enabled);
            int failed = ctx.Remotes.States.Count(s => s.Connection.Enabled && s.Error != null);
            remoteBtn.Text = "远程连接" + (enabled == 0 ? "" : "（" + enabled + "）")
                + (failed == 0 ? "" : " · " + failed + " 项失败");
            LayoutContent();
        }

        void RebuildUpdateStrip()
        {
            Store store = ctx.Store;
            updateFlow.Controls.Clear();
            updateStrip.Visible = store.Update != UpdateState.Idle;
            if (!updateStrip.Visible) return;
            Font f = Program.UiFont(11);

            if (store.Update == UpdateState.Checking)
            {
                updateFlow.Controls.Add(MakeStripLabel("检查更新中…", Color.FromArgb(120, 120, 125), f));
            }
            else if (store.Update == UpdateState.Updating)
            {
                updateFlow.Controls.Add(MakeStripLabel("引擎更新中…", Color.FromArgb(120, 120, 125), f));
            }
            else if (store.Update == UpdateState.Result)
            {
                if (!String.IsNullOrEmpty(store.UpdateError))
                    updateFlow.Controls.Add(MakeStripLabel("⚠ " + store.UpdateError, Color.FromArgb(200, 130, 0), f));
                if (store.EngineUpdate != null)
                {
                    FlowLayoutPanel row = MakeStripRow();
                    row.Controls.Add(MakeStripLabel(
                        String.Format("引擎 ccusage 新版 {0}（当前 {1}）", store.EngineUpdate.Latest, store.EngineUpdate.Installed),
                        Color.FromArgb(40, 40, 45), f));
                    Button b = MakeStripButton("更新引擎");
                    b.Click += delegate { ctx.PerformEngineUpdate(); };
                    row.Controls.Add(b);
                    updateFlow.Controls.Add(row);
                }
                if (store.AppUpdate != null)
                {
                    FlowLayoutPanel row = MakeStripRow();
                    row.Controls.Add(MakeStripLabel(
                        String.Format("UsageBar 新版 {0}（当前 {1}）", store.AppUpdate.Latest, store.AppUpdate.Installed),
                        Color.FromArgb(40, 40, 45), f));
                    Button b = MakeStripButton("下载更新");
                    b.Click += delegate { ctx.OpenAppRelease(); };
                    row.Controls.Add(b);
                    updateFlow.Controls.Add(row);
                }
                if (store.EngineUpdate == null && store.AppUpdate == null && String.IsNullOrEmpty(store.UpdateError))
                {
                    updateFlow.Controls.Add(MakeStripLabel("√ 均为最新版本", Color.FromArgb(40, 160, 70), f));
                }
            }
        }

        Label MakeStripLabel(string text, Color color, Font f)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = f;
            l.ForeColor = color;
            l.AutoSize = true;
            l.Margin = new Padding(Program.Px(2), Program.Px(1), Program.Px(2), Program.Px(1));
            return l;
        }

        FlowLayoutPanel MakeStripRow()
        {
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.FlowDirection = FlowDirection.LeftToRight;
            row.AutoSize = true;
            row.WrapContents = false;
            row.Margin = new Padding(0);
            return row;
        }

        Button MakeStripButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.Font = Program.UiFont(11);
            b.AutoSize = false;
            b.Size = new Size(TextRenderer.MeasureText(text, Program.UiFont(11)).Width + Program.Px(14), Program.Px(22));
            b.Cursor = Cursors.Hand;
            b.FlatStyle = FlatStyle.System;
            b.Margin = new Padding(Program.Px(6), 0, 0, 0);
            return b;
        }

        void LayoutContent()
        {
            // branch on DATA STATE, not control.Visible — the getter returns the
            // parent-chain result (false while the form is hidden), which made the
            // pre-Show DataBind compute a broken layout that flashed for a second
            // after every open until the refresh completed and relaid it out
            bool missing = ctx.Store.EngineMissing;
            bool showStrip = ctx.Store.Update != UpdateState.Idle;

            Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            int pad = Program.Px(14);
            int w = Math.Min(Program.Px(380), workingArea.Width - Program.Px(16));
            int innerW = w - pad * 2;
            int y = pad;
            int segH = Program.Px(26);
            // 保持画幅比例，并为列表、更新提示和底栏预留屏幕高度。
            int otherH = pad * 2 + segH + list.AutoHeight + Program.Px(64)
                + Program.Px(10) + Program.Px(8) * 3 + Program.Px(6) + 1
                + (showStrip ? updateFlow.Height + Program.Px(8) : 0);
            int chartH = Math.Max(Program.Px(80), Math.Min((int)Math.Round(innerW * 172.0 / 352),
                workingArea.Height - Program.Px(16) - otherH));

            if (!missing)
            {
                segAgent.Bounds = new Rectangle(pad, y, Program.Px(68), segH);
                segModel.Bounds = new Rectangle(pad + Program.Px(70), y, Program.Px(68), segH);
                seriesBtn.Bounds = new Rectangle(pad + innerW - seriesBtn.Width, y, seriesBtn.Width, segH);
                y += segH + Program.Px(10);

                chart.Bounds = new Rectangle(pad, y, innerW, chartH);
                y += chartH + Program.Px(8);

                divider.Bounds = new Rectangle(pad, y, innerW, 1);
                y += Program.Px(8);

                list.Bounds = new Rectangle(pad, y, innerW, list.AutoHeight);
                y += list.Height + Program.Px(6);
            }
            // child panels need explicit bounds — without them the default
            // 200x100 clips the chart/list to a corner
            mainContent.Bounds = new Rectangle(0, 0, w, y);

            if (missing)
            {
                int cardPad = Program.Px(10);
                engineTitle.Location = new Point(cardPad, cardPad);
                engineDesc.Location = new Point(cardPad, engineTitle.Bottom + Program.Px(6));
                int flowY = engineDesc.Bottom + Program.Px(8);
                int blockH;
                if (ctx.Store.HasNpm && !ctx.Store.EngineInstalling)  // same rule as DataBind's engineNpmBtn.Visible
                {
                    engineNpmBtn.Location = new Point(cardPad, flowY);
                    engineStatus.Location = new Point(cardPad + engineNpmBtn.Width + Program.Px(8), flowY + Program.Px(4));
                    blockH = Math.Max(engineNpmBtn.Bottom, engineStatus.Bottom);
                }
                else
                {
                    engineStatus.Location = new Point(cardPad, flowY);
                    blockH = engineStatus.Bottom;
                }
                engineCard.Bounds = new Rectangle(pad, y, innerW, blockH + cardPad);
                y += engineCard.Height + Program.Px(6);
            }

            if (showStrip)
            {
                updateFlow.Location = new Point(Program.Px(2), Program.Px(2));
                updateStrip.Bounds = new Rectangle(pad, y, innerW, updateFlow.Height + Program.Px(4));
                y += updateStrip.Height + Program.Px(4);
            }

            int bottomH = Program.Px(64);
            int bottomY = y + Program.Px(8);
            bottomBar.Bounds = new Rectangle(0, bottomY, w, bottomH);
            int bx = pad + innerW;
            int by = (Program.Px(30) - quitBtn.Height) / 2;
            quitBtn.Location = new Point(bx - quitBtn.Width, by);
            bx -= quitBtn.Width + Program.Px(6);
            checkBtn.Location = new Point(bx - checkBtn.Width, by);
            bx -= checkBtn.Width + Program.Px(6);
            refreshBtn.Location = new Point(bx - refreshBtn.Width, by);
            bx -= refreshBtn.Width + Program.Px(10);
            autostartCheck.Location = new Point(bx - autostartCheck.Width, by + Program.Px(2));
            dailyBtn.Location = new Point(pad, by);
            remoteBtn.Bounds = new Rectangle(pad, Program.Px(34), innerW, Program.Px(26));

            ClientSize = new Size(w, bottomY + bottomH + pad);
        }

        // open just above the taskbar, horizontally near the cursor (Razer Synapse
        // style: the panel appears where you clicked the tray icon)
        public void PositionNearCursor()
        {
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int x = Cursor.Position.X - Width / 2;
            int yTop = wa.Bottom - Height - Program.Px(8);
            if (x < wa.Left + Program.Px(8)) x = wa.Left + Program.Px(8);
            if (x + Width > wa.Right) x = wa.Right - Width - Program.Px(8);
            if (yTop < wa.Top) yTop = wa.Top + Program.Px(8);
            Location = new Point(x, yTop);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Hide();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // MARK: - App context (tray icon + menu + refresh loop + update checks)

    class AppContext : ApplicationContext
    {
        public readonly Settings Settings;
        public readonly Store Store;
        public readonly RemoteSources Remotes;
        RemoteConnectionsForm remoteDialog;
        Payload localPayload;
        bool localEngineMissing;
        public bool RemoteDialogOpen { get { return remoteDialog != null; } }

        readonly SynchronizationContext ui;
        NotifyIcon tray;
        FlyoutForm flyout;
        MessageWindow msgWin;
        System.Windows.Forms.Timer refreshTimer;   // 60s data refresh
        bool busy;

        public AppContext()
        {
            // 将后台结果切回 WinForms 主线程，避免更新控件时引发跨线程异常。
            ui = new WindowsFormsSynchronizationContext();
            Settings = Settings.Load();
            Store = new Store();
            Remotes = new RemoteSources(Settings, System.IO.Path.Combine(Settings.DirectoryPath(), "remote-cache.json"));

            msgWin = new MessageWindow(this);
            Program.Log("ctor: msgWin created");

            tray = new NotifyIcon();
            tray.Icon = Bolt.MakeTrayIcon();
            tray.Text = "UsageBar";
            tray.Visible = true;
            tray.ContextMenuStrip = BuildTrayMenu();
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleFlyout();
            };

            flyout = new FlyoutForm(this);
            ApplyCombined();
            Program.Log("ctor: flyout created");

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 60000;
            refreshTimer.Tick += delegate { RefreshData(); };
            refreshTimer.Start();

            // 首次启动创建开始菜单和桌面快捷方式，并注册开机自启。
            if (!Settings.FirstRunDone)
            {
                CreateShortcuts();
                SetAutostart(true);
                Settings.FirstRunDone = true;
                Settings.Save();
            }

            RefreshData();
            Program.Log("ctor: done");

            if (Environment.GetEnvironmentVariable("USAGEBAR_AUTOSHOW") == "1")
            {
                // must tick on the UI thread — a Threading.Timer would Show() the
                // flyout from a pool thread where it never becomes visible.
                // Opens twice so cold vs warm open timings can be compared.
                int opens = 0;
                System.Windows.Forms.Timer autoshow = new System.Windows.Forms.Timer();
                autoshow.Interval = 3000;
                autoshow.Tick += delegate
                {
                    opens++;
                    ShowFlyout();
                    if (opens >= 2) autoshow.Stop();
                };
                autoshow.Start();
            }
            if (Environment.GetEnvironmentVariable("USAGEBAR_FAKE_NO_ENGINE") == "1")
            {
                Store.EngineMissing = true;
            }

            // pre-create the flyout's window handle (off-screen, then hidden) so
            // the first tray click doesn't pay handle-creation + JIT cost
            flyout.Location = new Point(-3000, -3000);
            flyout.Show();
            flyout.Hide();
        }

        ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem daily = new ToolStripMenuItem("终端日报");
            daily.Click += delegate { OpenDailyReport(); };
            menu.Items.Add(daily);

            ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { RefreshData(true); };
            menu.Items.Add(refresh);

            ToolStripMenuItem check = new ToolStripMenuItem("检查更新");
            check.Click += delegate
            {
                ShowFlyout();
                CheckForUpdate();
            };
            menu.Items.Add(check);

            ToolStripMenuItem autostart = new ToolStripMenuItem("开机自启");
            autostart.Checked = IsAutostartOn();
            autostart.Click += delegate
            {
                SetAutostart(!IsAutostartOn());
                autostart.Checked = IsAutostartOn();
                if (flyout.Visible) flyout.DataBind();
            };
            menu.Items.Add(autostart);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem quit = new ToolStripMenuItem("退出");
            quit.Click += delegate { Quit(); };
            menu.Items.Add(quit);
            return menu;
        }

        // MARK: autostart (HKCU Run — no admin needed, same as claude-usage-tray et al.)

        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsAutostartOn()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                return k != null && k.GetValue("UsageBar") != null;
            }
        }

        public void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (on) k.SetValue("UsageBar", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("UsageBar") != null) k.DeleteValue("UsageBar");
                }
            }
            catch { }
        }

        // 首次启动创建开始菜单和桌面快捷方式。

        static void CreateShortcuts()
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType);
                string exe = Application.ExecutablePath;
                string[] dirs = new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };
                foreach (string dir in dirs)
                {
                    try
                    {
                        string lnkPath = System.IO.Path.Combine(dir, "UsageBar.lnk");
                        if (File.Exists(lnkPath)) continue;   // 保留用户现有快捷方式。
                        dynamic lnk = shell.CreateShortcut(lnkPath);
                        lnk.TargetPath = exe;
                        lnk.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
                        lnk.IconLocation = exe + ",0";
                        lnk.Description = "UsageBar — AI CLI token 用量表";
                        lnk.Save();
                    }
                    catch { }
                }
            }
            catch { }
        }

        // MARK: data refresh

        DateTime lastRefreshAt = DateTime.MinValue;

        // force=true for explicit user actions (刷新按钮/菜单/引擎更新); the 60s
        // timer and panel-open path are throttled so a tray click never kicks off
        // a node process unless the data is actually stale
        public void RefreshData() { RefreshData(false); }

        public void RefreshData(bool force)
        {
            if (!force && (DateTime.Now - lastRefreshAt).TotalSeconds < 15) return;
            foreach (RemoteState state in Remotes.States.ToArray()) RefreshRemote(state, false);
            if (busy) return;
            busy = true;
            lastRefreshAt = DateTime.Now;
            bool needAgents = Store.SupportedAgents.Count == 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Payload payload = Environment.GetEnvironmentVariable("USAGEBAR_FAKE_NO_ENGINE") == "1"
                    ? null
                    : Ccusage.RunJSON();
                bool engineInstalled = (payload != null || Ccusage.Installed())
                    && Environment.GetEnvironmentVariable("USAGEBAR_FAKE_NO_ENGINE") != "1";  // test hook: pretend the engine is missing
                List<string> agents = needAgents ? Ccusage.ParseAgents() : null;
                ui.Post(delegate
                {
                    busy = false;
                    localEngineMissing = !engineInstalled;
                    if (payload == null)
                    {
                        if (localEngineMissing) DetectInstallers();
                    }
                    else
                    {
                        localPayload = payload;
                        if (agents != null && agents.Count > 0) Store.SupportedAgents = agents;
                    }
                    ApplyCombined();
                }, null);
            });
        }

        void ApplyCombined()
        {
            bool hasData = localPayload != null || Remotes.States.Any(s => s.Connection.Enabled && s.Data != null);
            if (hasData)
            {
                try
                {
                    Payload payload = Remotes.Combine(localPayload, UsageMerge.Today);
                    Store.EngineMissing = false;
                    Store.EngineError = null;
                    Store.AllDays = payload.Days;
                    Store.Points = payload.Days.Skip(Math.Max(0, payload.Days.Count - 7)).ToList();
                    Store.KnownModels = new HashSet<string>(payload.Days.SelectMany(d => d.Models.Keys), StringComparer.OrdinalIgnoreCase);
                    Store.Total = payload.Total;
                    Store.Title = Fmt.Human(payload.Total);
                }
                catch (Exception ex) { Remotes.CacheError = "用量无法合并：" + ex.GetType().Name; }
            }
            else
            {
                Store.EngineMissing = localEngineMissing;
                Store.Total = 0;
                Store.Title = "n/a";
                Store.AllDays = new List<DayPoint>();
                Store.Points = new List<DayPoint>();
                Store.KnownModels.Clear();
            }
            tray.Text = "UsageBar — 今日 " + Store.Title + " tokens";
            UpdateRemoteViews();
        }

        void UpdateRemoteViews()
        {
            if (remoteDialog != null && !remoteDialog.IsDisposed) remoteDialog.Rebind();
            if (flyout != null && flyout.Visible)
            {
                flyout.DataBind();
                flyout.PositionNearCursor();
            }
        }

        void RefreshRemote(RemoteState state, bool test)
        {
            int generation = Remotes.Begin(state, test);
            if (generation < 0) return;
            RemoteConnection connection = new RemoteConnection { Alias = state.Connection.Alias, System = state.Connection.System };
            UpdateRemoteViews();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Payload payload = null;
                string error = null;
                try
                {
                    DateTime today = UsageMerge.Today;
                    Ccusage.RunResult result = RemoteEngine.Run(connection, today);
                    if (result.Code != 0) error = RemoteEngine.Error(result);
                    else payload = Ccusage.ParseJSON(result.Out, today, false);
                }
                catch (Exception ex) { error = "远端用量数据无效：" + ex.GetType().Name; }
                ui.Post(delegate
                {
                    if (Remotes.Complete(state, generation, payload, error)) ApplyCombined();
                }, null);
            });
        }

        void RemoteConfigurationChanged()
        {
            Settings.Save();
            ApplyCombined();
            foreach (RemoteState state in Remotes.States.ToArray()) RefreshRemote(state, false);
        }

        public void OpenRemoteConnections()
        {
            if (remoteDialog != null) { remoteDialog.Activate(); return; }
            remoteDialog = new RemoteConnectionsForm(Remotes,
                delegate(RemoteState state) { RefreshRemote(state, true); }, RemoteConfigurationChanged);
            try { remoteDialog.ShowDialog(flyout); }
            finally
            {
                remoteDialog.Dispose();
                remoteDialog = null;
            }
        }

        // MARK: flyout

        public void ToggleFlyout()
        {
            if (flyout.Visible)
            {
                flyout.Hide();
                return;
            }
            // clicking the tray icon while the flyout is open just closed it via
            // Deactivate — don't immediately reopen (same toggle semantics as mac)
            if ((DateTime.Now - flyout.LastDeactivateClose).TotalMilliseconds < 400) return;
            ShowFlyout();
        }

        public void ShowFlyout()
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            RefreshData(false);
            flyout.DataBind();
            long t1 = sw.ElapsedMilliseconds;
            flyout.PositionNearCursor();
            flyout.Show();
            long t2 = sw.ElapsedMilliseconds;
            flyout.Activate();
            // only log slow opens — the timing is for diagnosing "打开慢"
            if (sw.ElapsedMilliseconds > 150)
                Program.Log("ShowFlyout: databind=" + t1 + "ms show=" + (t2 - t1) + "ms total=" + sw.ElapsedMilliseconds + "ms");
            Program.Log("ShowFlyout: flyoutLoc=" + flyout.Location + " flyoutSize=" + flyout.Size + " cursor=" + Cursor.Position);
        }

        public void SwitchMode(string mode)
        {
            Settings.Mode = mode;
            Settings.Save();
            if (flyout.Visible) flyout.DataBind();
        }

        public void ReorderSeries(string dragged, string target)
        {
            string mode = Settings.Mode;
            List<string> arr = ViewLogic.ListSeries(Settings, Store, mode);
            int from = arr.IndexOf(dragged);
            int to = arr.IndexOf(target);
            if (from < 0 || to < 0 || from == to) return;
            arr.RemoveAt(from);
            arr.Insert(to, dragged);
            Settings.SetOrderFor(mode, String.Join(",", arr.ToArray()));
            Settings.Save();
        }

        // MARK: engine install / update

        public void DetectInstallers()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool hasNpm = Ccusage.Run("npm --version", 8000).Code == 0;
                ui.Post(delegate
                {
                    Store.HasNpm = hasNpm;
                    if (flyout.Visible) flyout.DataBind();
                }, null);
            });
        }

        public void InstallEngine()
        {
            if (Store.EngineInstalling) return;
            Store.EngineInstalling = true;
            Store.EngineError = null;
            if (flyout.Visible) flyout.DataBind();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Ccusage.RunResult r = Ccusage.Run("npm install -g ccusage", 600000);
                ui.Post(delegate
                {
                    Store.EngineInstalling = false;
                    if (r.Code == 0)
                    {
                        Store.EngineMissing = false;
                        RefreshData(true);
                    }
                    else
                    {
                        Store.EngineError = Tail(r.Err, 3);
                    }
                    if (flyout.Visible) flyout.DataBind();
                }, null);
            });
        }

        public void CheckForUpdate()
        {
            if (Store.Update == UpdateState.Checking || Store.Update == UpdateState.Updating) return;
            Store.Update = UpdateState.Checking;
            if (flyout.Visible) flyout.DataBind();
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateItem engine = null;
                UpdateItem app = null;
                string error = null;

                // engine side: installed ccusage vs npm registry
                if (Ccusage.Installed())
                {
                    Ccusage.RunResult inst = Ccusage.Run("ccusage --version", 15000);
                    string installed = inst.Code == 0 ? Ver.VersionToken(inst.Out) : null;
                    string reg = HttpGet("https://registry.npmjs.org/ccusage/latest", 5000);
                    string latest = ParseJsonField(reg, "version");
                    if (installed != null && latest != null && Ver.Newer(latest, installed))
                        engine = new UpdateItem(installed, latest);
                }

                // app side: this repo's latest GitHub release
                string gh = HttpGet("https://api.github.com/repos/zquickm/UsageBar/releases/latest", 5000);
                if (gh != null)
                {
                    string tag = ParseJsonField(gh, "tag_name");
                    string latestApp = tag != null ? Ver.VersionToken(tag) : null;
                    if (latestApp != null && Ver.Newer(latestApp, Program.AppVersion))
                        app = new UpdateItem(Program.AppVersion, latestApp);
                }
                else
                {
                    error = "无法访问 GitHub / npm";
                }

                ui.Post(delegate
                {
                    Store.Update = UpdateState.Result;
                    Store.EngineUpdate = engine;
                    Store.AppUpdate = app;
                    Store.UpdateError = error;
                    if (flyout.Visible) flyout.DataBind();
                    flyout.dismissTimer.Stop();
                    flyout.dismissTimer.Start();
                }, null);
            });
        }

        public void PerformEngineUpdate()
        {
            Store.Update = UpdateState.Updating;
            if (flyout.Visible) flyout.DataBind();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Ccusage.RunResult r = Ccusage.Run("npm install -g ccusage@latest", 600000);
                ui.Post(delegate
                {
                    if (r.Code == 0)
                    {
                        Store.SupportedAgents = new List<string>();  // re-parse from the new ccusage
                        RefreshData(true);
                        CheckForUpdate();
                    }
                    else
                    {
                        Store.Update = UpdateState.Result;
                        Store.EngineUpdate = null;
                        Store.AppUpdate = null;
                        Store.UpdateError = Tail(r.Err, 2);
                        if (flyout.Visible) flyout.DataBind();
                        flyout.dismissTimer.Stop();
                        flyout.dismissTimer.Start();
                    }
                }, null);
            });
        }

        public void OpenAppRelease()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/zquickm/UsageBar/releases/latest") { UseShellExecute = true });
            }
            catch { }
        }

        // MARK: misc actions

        public void OpenDailyReport()
        {
            try
            {
                string wt = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WindowsApps\wt.exe");
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.UseShellExecute = true;
                if (File.Exists(wt))
                {
                    psi.FileName = wt;
                    psi.Arguments = "cmd /k ccusage daily";
                }
                else
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/k ccusage daily";
                }
                Process.Start(psi);
            }
            catch { }
        }

        public void Quit()
        {
            tray.Visible = false;
            Application.Exit();
        }

        public void OnTaskbarCreated()
        {
            // explorer restarted: re-add the tray icon (tray icons vanish with it)
            try
            {
                tray.Visible = false;
                tray.Visible = true;
            }
            catch { }
        }

        // MARK: helpers

        static string Tail(string s, int lines)
        {
            if (String.IsNullOrEmpty(s)) return "";
            string[] parts = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= lines) return s.Trim();
            return String.Join("\n", parts, parts.Length - lines, lines).Trim();
        }

        static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "UsageBar-Windows/" + Program.AppVersion;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        // tiny field extraction for the two update endpoints (avoids full JSON typing)
        static string ParseJsonField(string json, string field)
        {
            if (json == null) return null;
            string key = "\"" + field + "\"";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + key.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length) { i++; sb.Append(json[i]); }
                else sb.Append(json[i]);
                i++;
            }
            return sb.ToString();
        }
    }
}
