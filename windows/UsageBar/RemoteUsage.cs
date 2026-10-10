using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace UsageBar
{
    class RemoteConnection
    {
        public string Alias;
        public string System = "linux";
        public bool Enabled = true;
    }

    class RemoteState
    {
        public RemoteConnection Connection;
        public Payload Data;
        public DateTime LastSuccessUtc;
        public string Error;
        public bool Busy;
        public int Generation;
    }

    static class UsageMerge
    {
        public static DateTime Today
        {
            get { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "China Standard Time").Date; }
        }

        public static Payload Combine(IEnumerable<Payload> sources, DateTime today)
        {
            List<Payload> snapshots = sources.Where(s => s != null && s.Days != null).ToList();
            Dictionary<string, string> agentNames = Names(snapshots, true);
            Dictionary<string, string> modelNames = Names(snapshots, false);
            Dictionary<string, DayPoint> days = new Dictionary<string, DayPoint>();
            for (DateTime day = today.AddDays(-3649); day <= today; day = day.AddDays(1))
            {
                string date = DateKey(day);
                days[date] = new DayPoint(date);
            }
            foreach (Payload source in snapshots)
            {
                if (source == null || source.Days == null) continue;
                foreach (DayPoint point in source.Days)
                {
                    DayPoint target;
                    if (point == null || !days.TryGetValue(point.Date ?? "", out target)) continue;
                    Add(target.Agents, point.Agents, agentNames);
                    Add(target.Models, point.Models, modelNames);
                }
            }
            return new Payload { Days = days.Values.OrderBy(d => d.Date, StringComparer.Ordinal).ToList(),
                Total = days[DateKey(today)].Agents.Values.Sum() };
        }

        public static string DateKey(DateTime date) { return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        static Dictionary<string, string> Names(List<Payload> snapshots, bool agents)
        {
            // 先收集本机全历史的名称，防止较早的远端拼写改变既有勾选和配色。
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Payload snapshot in snapshots)
                foreach (DayPoint day in snapshot.Days)
                {
                    if (day == null) continue;
                    Dictionary<string, long> values = agents ? day.Agents : day.Models;
                    if (values == null) throw new FormatException("缓存分类缺失");
                    foreach (string name in values.Keys)
                        if (!names.ContainsKey(name)) names[name] = name;
                }
            return names;
        }

        static void Add(Dictionary<string, long> target, Dictionary<string, long> source, Dictionary<string, string> names)
        {
            if (source == null) throw new FormatException("缓存分类缺失");
            foreach (KeyValuePair<string, long> entry in source)
            {
                if (String.IsNullOrWhiteSpace(entry.Key) || entry.Value < 0) throw new FormatException("缓存用量无效");
                long previous;
                string name = names[entry.Key];
                target.TryGetValue(name, out previous);
                target[name] = checked(previous + entry.Value);
            }
        }
    }

    class RemoteCacheEntry
    {
        public string Alias;
        public string System;
        public Payload Data;
        public DateTime LastSuccessUtc;
    }

    class RemoteSources
    {
        public readonly List<RemoteState> States = new List<RemoteState>();
        readonly Settings settings;
        readonly string cachePath;
        public string CacheError;

        public RemoteSources(Settings settings, string cachePath)
        {
            this.settings = settings;
            this.cachePath = cachePath;
            List<RemoteConnection> connections = settings.RemoteConnections ?? new List<RemoteConnection>();
            settings.RemoteConnections = new List<RemoteConnection>();
            foreach (RemoteConnection connection in connections)
            {
                if (connection == null) continue;
                try
                {
                    RemoteState state = Add(connection.Alias, connection.System);
                    state.Connection.Enabled = connection.Enabled;
                }
                catch (ArgumentException) { Program.Log("忽略无效或重复的远程连接配置"); }
            }
            try
            {
                if (!File.Exists(cachePath)) return;
                JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                List<RemoteCacheEntry> entries = json.Deserialize<List<RemoteCacheEntry>>(File.ReadAllText(cachePath));
                foreach (RemoteCacheEntry entry in entries ?? new List<RemoteCacheEntry>())
                {
                    if (entry == null) continue;
                    RemoteState state = States.Find(s => String.Equals(s.Connection.Alias, entry.Alias,
                        StringComparison.OrdinalIgnoreCase) && s.Connection.System == entry.System);
                    if (state == null || entry.Data == null) continue;
                    try
                    {
                        state.Data = UsageMerge.Combine(new[] { entry.Data }, UsageMerge.Today);
                        state.LastSuccessUtc = entry.LastSuccessUtc;
                    }
                    catch (Exception ex) { state.Error = "远程缓存无效：" + ex.GetType().Name; }
                }
            }
            catch (Exception ex) { CacheError = "远程缓存无法读取：" + ex.GetType().Name; }
        }

        public static void Validate(string alias, string system)
        {
            if (alias == null || !Regex.IsMatch(alias, "\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\\z"))
                throw new ArgumentException("SSH 别名只能包含字母、数字、点、下划线和连字符，且不能以连字符开头");
            if (system != "linux" && system != "windows") throw new ArgumentException("请选择 Linux 或 Windows");
        }

        public RemoteState Add(string alias, string system)
        {
            alias = (alias ?? "").Trim();
            Validate(alias, system);
            if (States.Any(s => String.Equals(s.Connection.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("此 SSH 别名已经添加");
            RemoteConnection connection = new RemoteConnection { Alias = alias, System = system };
            RemoteState state = new RemoteState { Connection = connection };
            settings.RemoteConnections.Add(connection);
            States.Add(state);
            return state;
        }

        public void Remove(RemoteState state)
        {
            state.Generation++;
            States.Remove(state);
            settings.RemoteConnections.Remove(state.Connection);
            SaveCache();
        }

        public void SetEnabled(RemoteState state, bool enabled)
        {
            if (state.Connection.Enabled == enabled) return;
            state.Generation++;
            state.Busy = false;
            state.Connection.Enabled = enabled;
        }

        public int Begin(RemoteState state, bool test = false)
        {
            if (!States.Contains(state) || state.Busy || (!state.Connection.Enabled && !test)) return -1;
            state.Busy = true;
            return ++state.Generation;
        }

        public bool Complete(RemoteState state, int generation, Payload data, string error)
        {
            // 停用、删除或重新发起请求后，旧结果不得覆盖当前快照。
            if (!States.Contains(state) || generation != state.Generation) return false;
            state.Busy = false;
            state.Error = error;
            if (data != null)
            {
                try { data = UsageMerge.Combine(new[] { data }, UsageMerge.Today); }
                catch (Exception ex) { state.Error = "远程日报无效：" + ex.GetType().Name; return true; }
                state.Data = data;
                state.LastSuccessUtc = DateTime.UtcNow;
                state.Error = null;
                SaveCache();
            }
            return true;
        }

        public Payload Combine(Payload local, DateTime today)
        {
            return UsageMerge.Combine(new[] { local }.Concat(States.Where(s => s.Connection.Enabled).Select(s => s.Data)), today);
        }

        public void SaveCache()
        {
            string temporary = cachePath + ".tmp";
            try
            {
                JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                List<RemoteCacheEntry> entries = States.Where(s => s.Data != null).Select(s => new RemoteCacheEntry {
                    Alias = s.Connection.Alias, System = s.Connection.System, Data = s.Data, LastSuccessUtc = s.LastSuccessUtc }).ToList();
                File.WriteAllText(temporary, json.Serialize(entries), new UTF8Encoding(false));
                if (File.Exists(cachePath)) File.Replace(temporary, cachePath, null);
                else File.Move(temporary, cachePath);
                CacheError = null;
            }
            catch (Exception ex) { CacheError = "远程缓存无法保存：" + ex.GetType().Name; }
        }
    }

    static class RemoteEngine
    {
        public static string Command(RemoteConnection connection, DateTime today)
        {
            RemoteSources.Validate(connection.Alias, connection.System);
            string options = "daily --since " + UsageMerge.DateKey(today.AddDays(-3649))
                + " --until " + UsageMerge.DateKey(today) + " --timezone Asia/Shanghai --offline --json --by-agent";
            if (connection.System == "linux")
            {
                // 清空目录覆盖变量，并使用空配置，避免读取登录账号之外的数据根目录。
                string script = "set -e\ncd \"$HOME\"\n"
                    + "if test -x \"$HOME/.local/bin/ccusage\"; then engine=\"$HOME/.local/bin/ccusage\"; else engine=$(command -v ccusage || true); fi\n"
                    + "if test -z \"$engine\"; then printf 'USAGEBAR_CCUSAGE_MISSING\\n' >&2; exit 127; fi\n"
                    + "config=$(mktemp)\ntrap 'rm -f \"$config\"' EXIT\nprintf '{}' > \"$config\"\n"
                    + "env -i HOME=\"$HOME\" PATH=\"$PATH\" LANG=C.UTF-8 USER=\"$(id -un)\" "
                    + "\"$engine\" " + options + " --config \"$config\"\n";
                return "printf %s '" + Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) + "' | base64 -d | sh";
            }
            string windows = "$ErrorActionPreference='Stop'\n$ProgressPreference='SilentlyContinue'\n"
                + "$homePath=[Environment]::GetFolderPath('UserProfile')\nSet-Location -LiteralPath $homePath\n"
                + "Get-ChildItem Env: | Where-Object { $_.Name -match '^(CODEX_HOME|ZCODE_HOME|CLAUDE_CONFIG_DIR|XDG_.*|.*_DATA_DIR)$' } | ForEach-Object { [Environment]::SetEnvironmentVariable($_.Name,$null,'Process') }\n"
                + "$env:HOME=$homePath\n$env:USERPROFILE=$homePath\n"
                + "if (Test-Path -LiteralPath (Join-Path $homePath '.codex')) { $env:CODEX_HOME=Join-Path $homePath '.codex' }\n"
                + "if (Test-Path -LiteralPath (Join-Path $homePath '.zcode')) { $env:ZCODE_HOME=Join-Path $homePath '.zcode' }\n"
                + "if (Test-Path -LiteralPath (Join-Path $homePath '.claude\\projects')) { $env:CLAUDE_CONFIG_DIR=Join-Path $homePath '.claude' }\n"
                + "$env:PYTHONIOENCODING='utf-8'\n[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)\n"
                + "$engine=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'npm\\ccusage.cmd'\n"
                + "if (-not (Test-Path -LiteralPath $engine)) { $found=Get-Command ccusage.cmd,ccusage.exe -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $found) { [Console]::Error.WriteLine('USAGEBAR_CCUSAGE_MISSING'); exit 127 }; $engine=$found.Source }\n"
                + "$config=[IO.Path]::GetTempFileName()\ntry { [IO.File]::WriteAllText($config,'{}',(New-Object Text.UTF8Encoding($false))); & $engine "
                + options + " --config $config; exit $LASTEXITCODE } finally { Remove-Item -LiteralPath $config -Force }\n";
            return "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(windows));
        }

        public static string Quote(string value)
        {
            // 按 Windows 参数规则转义，不能把用户输入交给 cmd.exe 拼接。
            return "\"" + Regex.Replace(value, @"(\\*)(""|$)", delegate(Match match)
            {
                return match.Groups[1].Value + match.Groups[1].Value
                    + (match.Groups[2].Value == "\"" ? "\\\"" : "");
            }) + "\"";
        }

        public static Ccusage.RunResult Run(RemoteConnection connection, DateTime today)
        {
            string command = Command(connection, today);
            return Ccusage.RunProcess("ssh.exe", "-T -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10 "
                + "-o ServerAliveInterval=5 -o ServerAliveCountMax=2 -- " + Quote(connection.Alias) + " " + Quote(command), 30000);
        }

        public static string Error(Ccusage.RunResult result)
        {
            string text = result.Err ?? "";
            if (text.Contains("USAGEBAR_CCUSAGE_MISSING")) return "远端未安装 ccusage";
            if (text.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
                return "SSH 认证失败，请配置密钥或 ssh-agent 免交互登录";
            if (text.IndexOf("Host key", StringComparison.OrdinalIgnoreCase) >= 0
                || text.Contains("REMOTE HOST IDENTIFICATION")) return "请先在终端核对 SSH 主机指纹";
            if (text.Contains("超时")) return "连接或统计超时（30 秒）";
            if (String.IsNullOrWhiteSpace(text)) return "SSH 返回错误：" + result.Code;
            return text.Trim().Substring(0, Math.Min(text.Trim().Length, 400));
        }
    }

    class RemoteConnectionsForm : Form
    {
        readonly RemoteSources sources;
        readonly Action<RemoteState> test;
        readonly Action changed;
        readonly DataGridView grid = new DataGridView();
        readonly TextBox alias = new TextBox();
        readonly ComboBox system = new ComboBox();
        readonly Label status = new Label();
        readonly ToolTip tips = new ToolTip();
        bool binding;

        public RemoteConnectionsForm(RemoteSources sources, Action<RemoteState> test, Action changed)
        {
            this.sources = sources; this.test = test; this.changed = changed;
            Text = "远程连接";
            Font = Program.UiFont(12);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(Program.Px(700), Program.Px(430));
            MinimumSize = new Size(Program.Px(550), Program.Px(320));
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 3, Padding = new Padding(Program.Px(12)) };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Program.Px(62)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Program.Px(76)));
            Controls.Add(layout);
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = Color.White;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "启用", Width = Program.Px(48),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SSH 别名", ReadOnly = true, FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "系统", ReadOnly = true, FillWeight = 55 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", ReadOnly = true, FillWeight = 95 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "最后成功", ReadOnly = true, FillWeight = 110 });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (binding || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                RemoteState state = (RemoteState)grid.Rows[e.RowIndex].Tag;
                sources.SetEnabled(state, Convert.ToBoolean(grid.Rows[e.RowIndex].Cells[0].Value));
                changed();
            };
            grid.SelectionChanged += delegate { UpdateStatus(); };
            layout.Controls.Add(grid, 0, 0);
            status.Dock = DockStyle.Fill;
            status.ForeColor = Color.FromArgb(175, 60, 40);
            layout.Controls.Add(status, 0, 1);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            actions.Controls.Add(new Label { Text = "SSH 别名", AutoSize = true, Margin = new Padding(0, Program.Px(8), Program.Px(5), 0) });
            alias.Width = Program.Px(125);
            tips.SetToolTip(alias, "使用系统 SSH 配置中的别名，例如 server99");
            actions.Controls.Add(alias);
            system.DropDownStyle = ComboBoxStyle.DropDownList;
            system.Items.AddRange(new object[] { "Linux", "Windows" });
            system.SelectedIndex = 0;
            system.Width = Program.Px(85);
            actions.Controls.Add(system);
            AddButton(actions, "添加", delegate
            {
                try
                {
                    RemoteState state = sources.Add(alias.Text, system.SelectedIndex == 0 ? "linux" : "windows");
                    alias.Clear(); changed(); test(state);
                }
                catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "远程连接", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            });
            AddButton(actions, "测试连接", delegate { RemoteState selected = Selected(); if (selected != null) test(selected); });
            AddButton(actions, "移除", delegate { RemoteState selected = Selected(); if (selected != null) { sources.Remove(selected); changed(); } });
            AddButton(actions, "关闭", delegate { Close(); });
            layout.Controls.Add(actions, 0, 2);
            Rebind();
        }

        void AddButton(FlowLayoutPanel panel, string text, Action action)
        {
            Button button = new Button { Text = text, AutoSize = true, Height = Program.Px(28),
                Margin = new Padding(Program.Px(3), 0, 0, 0) };
            button.Click += delegate { action(); };
            panel.Controls.Add(button);
        }

        RemoteState Selected() { return grid.CurrentRow == null ? null : grid.CurrentRow.Tag as RemoteState; }

        void UpdateStatus()
        {
            RemoteState selected = Selected();
            status.Text = sources.CacheError ?? (selected == null ? "" : selected.Error ?? "");
            tips.SetToolTip(status, status.Text);
        }

        public void Rebind()
        {
            RemoteState selected = Selected();
            binding = true;
            grid.Rows.Clear();
            foreach (RemoteState state in sources.States)
            {
                string text = !state.Connection.Enabled ? "已停用" : state.Busy ? "读取中"
                    : state.Error != null ? (state.Data == null ? "失败" : "失败（保留缓存）")
                    : state.Data == null ? "未连接" : "已获取";
                int row = grid.Rows.Add(state.Connection.Enabled, state.Connection.Alias,
                    state.Connection.System == "linux" ? "Linux" : "Windows", text,
                    state.LastSuccessUtc == DateTime.MinValue ? "—" : state.LastSuccessUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"));
                grid.Rows[row].Tag = state;
                grid.Rows[row].Cells[3].ToolTipText = state.Error ?? text;
                if (state == selected) grid.CurrentCell = grid.Rows[row].Cells[1];
            }
            binding = false;
            UpdateStatus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
