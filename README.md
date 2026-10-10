# UsageBar ⚡

macOS 菜单栏 / Windows 托盘的 AI CLI token 用量表：常驻显示今日用量，点开看 7 天曲线和精确数字，可按工具或按模型拆分。默认读取本机会话日志；Windows 还可通过 SSH 汇总远程机器的 ccusage 日报。不需要 API key，不上传到第三方服务。

A menu bar (macOS) / system tray (Windows) app that tracks your AI CLI token usage (Claude Code, Codex, ZCode, OpenCode, Gemini CLI, Kimi, Qwen … — everything [ccusage](https://github.com/ccusage/ccusage) supports). 7-day chart + exact numbers, local by default, with optional SSH aggregation on Windows.

**Windows 用户直接看 [Windows 版](#windows-版) 一节。**

![screenshot](docs/screenshot.png)

## 它能做什么

- **菜单栏常驻**：⚡ 后面是今日 token 总量（如 `⚡12.3万`），每 60 秒自动刷新
- **点开弹窗**：最近 7 天用量曲线 + 当日/7 天精确用量；悬停任意一天看当天的数；按天虚线网格
- **两种视角**：按工具（zcode / codex / claude …）或按模型（GLM-5.3 / gpt …）拆分，一键切换
- **想看谁就勾谁**：默认只显示总量；在工具/模型下拉里勾选即加入折线图，列表与图表完全同步，一键恢复默认
- **可定制**：用量列表拖拽排序（记住顺序）、点色块用系统色轮改颜色
- **终端日报**：底部「终端日报」按钮在 Terminal 打开用量报表；可选安装的 `usage` 命令还能在终端独立使用
- **开机自启**：面板内开关（登录项方式）
- **检查更新**：一个按钮查两处——引擎 ccusage 新版（npm registry，一键升级，自动跟随 brew/npm）和 UsageBar 新版（GitHub Release，一键跳转下载）；更新检查仅点击时联网，已启用的远程连接会每分钟通过 SSH 刷新

## Windows 版

同一份数据引擎（ccusage），Windows 上做成**系统托盘应用**（Razer Synapse 那种形态）：托盘常驻一枚 ⚡ 图标，左键点击弹出用量面板，右键出菜单。

**安装**：两种方式任选其一——

1. **免安装便携**：从 [Releases](../../releases/latest) 下载 **`UsageBar-v*.exe`**（约 100 KB），双击即用。首次运行会像常见桌面软件一样自动创建**开始菜单 + 桌面快捷方式**并添加开机自启。
2. **完整安装**（装到固定位置，可在「设置 → 应用」里卸载）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File windows\install.ps1                        # 源码构建 + 安装 + 启动
   powershell -ExecutionPolicy Bypass -File windows\install.ps1 -Exe UsageBar.exe      # 直接安装下载好的 exe
   powershell -ExecutionPolicy Bypass -File windows\install.ps1 -Uninstall             # 卸载
   ```

   安装到 `%LocalAppData%\Programs\UsageBar`（免管理员权限），创建开始菜单 + 桌面快捷方式，并在 设置 → 应用 / 控制面板「程序和功能」注册卸载入口；安装时移除下载标记。

**Windows 需先装 [Node.js LTS](https://nodejs.org/)**，并在任意终端执行一次 `npm i -g ccusage`（面板检测到引擎缺失时也会引导）。

- **托盘图标**：悬停 tooltip 显示今日用量；Explorer 重启后自动恢复图标
- **再次打开**：双击快捷方式或再次运行 exe，不弹「已经在运行」——直接唤出正在运行的 UsageBar 面板
- **弹出面板**（左键托盘图标）：按工具/按模型切换、系列勾选、7 天平滑折线图、逐日用量列表（跟随图表悬停显示对应那天各工具/模型的用量）、颜色自定义——与 macOS 版面板一致；Esc 或点击外部关闭
- **图表自适应**：单调曲线避免峰谷过冲，峰顶留白、日期刻度和画幅随可用空间及缩放比例调整；各系列共享按数据范围调整的坐标轴，量级差距大时压缩比例尺，精确数值以列表为准。
- **托盘右键菜单**：终端日报、立即刷新、检查更新、开机自启、退出
- **检查更新**：一个按钮查两处——ccusage 新版（npm registry，一键升级）与 UsageBar 本体（GitHub Release）
- **本机和远程用量**：本机默认读取；底部「远程连接」可添加多台 Linux / Windows 机器，通过 SSH 获取日报。同日期、同名工具和模型直接相加，名称忽略大小写，不增加服务器统计行。
- **断线缓存**：每 60 秒自动刷新，远端单次最多等待 30 秒；断线时保留上次成功日报，连接窗口显示失败状态和最后成功时间。停用或移除连接立即停止计入。
- 首次启动自动添加开机自启（HKCU Run，可随时在菜单里关闭），检测到 ccusage 未安装时面板引导一键 `npm i -g ccusage`
- 提示：Windows 默认把新托盘图标收进溢出区（^）。想让它常驻可见：右键任务栏 → 任务栏设置 → 其他系统托盘图标 → 打开 UsageBar
- 提示：从 Releases 下载的 exe 未做代码签名，首次运行 Windows 可能弹 SmartScreen 蓝色提示——点**「更多信息」→「仍要运行」**；用 `install.ps1 -Exe` 安装会自动去掉下载标记。

**构建（零依赖）**：Windows 10/11 自带 C# 编译器（csc.exe），不需要装任何 SDK：

```powershell
powershell -ExecutionPolicy Bypass -File windows\build.ps1          # 构建
powershell -ExecutionPolicy Bypass -File windows\build.ps1 -Run     # 构建并运行
```

产物：`windows\dist\UsageBar.exe`（单文件，带 ⚡ 图标与版本信息；Win10/11 直接运行，.NET Framework 4.8 系统预装）。图标 `windows/UsageBar/app.ico` 由仓库根的 `Icon.iconset` 生成。界面和本机引擎在 `windows/UsageBar/Program.cs`，远程连接与缓存在 `windows/UsageBar/RemoteUsage.cs`，均兼容 C# 5。数据引擎 ccusage 未装时面板会给出安装引导；没有 npm 时先装 [Node.js LTS](https://nodejs.org/)。

### Windows 远程连接

本机需要 Windows OpenSSH 客户端；远端需启用 SSH，并在自己的账号下安装 ccusage。连接使用系统 SSH 配置的别名，主机、端口、用户名、密钥和代理均由 SSH 管理；UsageBar 不保存密码或私钥内容，不跳过主机指纹验证。密码或密钥口令需要交互输入时，请先配置密钥或 ssh-agent。

先在终端运行 `ssh server99`：作用是验证自己的登录账号，并人工核对首次连接的主机指纹。然后点击底部「远程连接」，填写该别名、选择 Linux 或 Windows、点击「添加」。勾选决定是否参与汇总；「测试连接」立即读取日报，「移除」删除连接及其缓存。

Linux 优先使用 `~/.local/bin/ccusage`，否则从 PATH 查找。Windows 优先使用当前账号的 `%APPDATA%\npm\ccusage.cmd`，否则从 PATH 查找 `ccusage.cmd` 或 `ccusage.exe`。其他机器不会被自动安装或升级引擎。可以运行 `npm i -g ccusage@20.0.26`：作用是在该账号的 npm 全局目录安装已验证兼容的引擎版本，需要 Node.js 和 npm。

只传输 Token 日报，不复制会话数据库、提示词或凭据。远端使用登录账号的默认日志目录和空 ccusage 配置，不使用管理员权限，也不接受其他用户的数据路径。日期统一按北京时间分组；保留与本机一致的 3650 天发现窗口，图表仍显示近 7 天。本机自定义 `.dsh` 账本只在本机追加一次。

连接配置保存在 `%APPDATA%\UsageBar\settings.json`，日报缓存保存在同目录的 `remote-cache.json`；重启可恢复。同一会话若在两台机器都保存了日志，会按要求重复相加；也不要用多个 SSH 别名重复添加同一台机器。工具总量沿用 ccusage 的 `totalTokens`，模型沿用输入、输出、缓存创建和缓存读取分项之和，两者可能有细微差异。

远程测试命令（默认不联网、不改真实设置）：

```powershell
powershell -ExecutionPolicy Bypass -File windows\check-remote.ps1
powershell -ExecutionPolicy Bypass -File windows\check-remote.ps1 -Live -WindowsScript -UI
```

第一条检查解析、合并、缓存、异常与参数安全；第二条额外连接已配置的 `server99`，在本机执行 Windows 远端脚本，并检查 100%–200% DPI 下的界面。Windows 脚本验证不等于另一台 Windows 机器的 SSH 实机验证。

## 系统要求

| 依赖 | 说明 |
|---|---|
| macOS 14+ | SwiftUI Charts |
| ccusage | 数据聚合引擎。**没装也没关系**：应用首次打开会引导一键安装（Homebrew 或 npm），也可提前自己执行 `brew install ccusage` |
| python3 | 仅可选的「终端 usage 命令」需要，随 Xcode 命令行工具提供 |

## 安装（约 1 分钟）

**方式 A：Homebrew（推荐，无任何提示）**

```bash
brew install --cask zquickm/tap/usagebar
```

cask 安装时自动剥掉隔离标记，装完直接打开，没有 Gatekeeper 提示；以后 `brew upgrade` 即可更新。

**方式 B：直接下载**

1. 从 [Releases](../../releases) 下载 **`UsageBar-v*.dmg`**，打开，把 **UsageBar** 拖到右边的 **Applications** 文件夹（窗口里有图示），然后推出磁盘镜像。
2. 首次打开会有一次 Gatekeeper 提示（DMG 安装窗口底部就印着这段指引；应用是 ad-hoc 签名、未公证，属正常，只需一次）：
   - **macOS 15 (Sequoia) 及以上**：双击打开一次，在弹出的提示里选「完成」；然后到 **系统设置 → 隐私与安全性**，往下拉到「安全性」区，点 **「仍要打开」** 并确认。
   - **macOS 14**：右键 → 打开 → 确认。
   - 或者用终端一行解决：`xattr -cr /Applications/UsageBar.app`（顺带把提示也免了）。之后正常双击打开，不再询问。

**两种方式之后都一样**：应用检测到数据引擎 ccusage 未安装时，弹窗里会出现安装卡片——点 **「用 Homebrew 安装」**（或 npm），等几分钟（首次会连 Node 一起装）即可；两者都没有时卡片会给出手动指引。打开后一分钟内，菜单栏闪电图标即显示今日用量。首次启动可能弹出「UsageBar 想要控制系统事件」——点**允许**，那是它在添加开机自启（不想要可在面板里关掉「开机自启」）。

**可选终端增强**：想要 `usage` 命令（终端里的明细报表）再跑一次：

```bash
git clone https://github.com/zquickm/UsageBar && cd UsageBar && ./install.sh
```

它把 `usage` 装到 `~/.local/bin` 并确保 ccusage 就绪；只要 app 的话**完全不用跑**。

## 日常使用

- **菜单栏**：⚡ 后的数字就是今日总 token，悬停无操作，点击开/关弹窗。
- **弹窗里**：
  - 顶部切换 **按工具 / 按模型**；
  - 图表默认只画「总量」，点开下方工具/模型下拉**勾选**想看的系列；「恢复默认」回到只看总量；
  - 列表可**拖拽排序**（顺序会记住），点系列前的**色块**改颜色；
  - 鼠标悬停图表，面板显示那一天的日期和各系列精确用量；
  - 底部按钮：**终端日报**（Terminal 里看近 7 天明细表）、**开机自启**、**刷新**、**退出**。
- **终端里**（可选增强，`install.sh` 提供）：

```bash
usage          # 近 7 天明细表（CLI / dsh 分列，含成本）
usage 30       # 近 30 天
usage --bar    # ⚡ 今日摘要块（菜单栏同款格式）
```

面板里的「终端日报」按钮不依赖它：有 `usage` 就开 `usage 7`，没有就直接开 `ccusage daily`。

## 数据从哪来

```text
各 CLI 的本地会话日志 ──► ccusage（聚合, --offline）──► UsageBar.app（内置聚合，直接调用）
(~/.zcode, ~/.codex, ~/.claude …)                                    （菜单栏）
```

- 应用对每个启用的数据来源各发一次 ccusage 聚合调用（`ccusage daily --by-agent --offline`），在本机合并日报；可选的 `~/.dsh` 账本仅在本机追加。
- 支持哪些工具、出现过哪些模型，**完全跟随你安装的 ccusage**：新工具在 ccusage 认识它日志的那天自动出现，新模型用过的当天自动出现，UsageBar 不需要跟着发版。
- ccusage 以 `--offline` 模式运行，不联网取价。默认只读取本机；Windows 启用远程连接后，远端日报通过 SSH 加密传到本机，不发送到第三方服务。

## 常见问题

- **菜单栏显示 `n/a`？** 先在终端跑 `ccusage daily` 看有无数据或报错；也确认你确实用过受支持的 CLI、日志已生成。
- **Windows 缺少 Codex/GPT 用量？** 确认 `CODEX_HOME` 指向实际 Codex 数据目录。UsageBar 启动时会在进程未设置该变量时读取 Windows 用户级配置，再由 ccusage 读取会话日志并统计；目录配置变更后重启 UsageBar。
- **金额是 `$0`？** ccusage 离线定价数据里没有该模型的价格，token 数不受影响。
- **提示需要 python3？** 只有想用可选的终端 `usage` 命令才需要，执行 `xcode-select --install` 即可。
- **我用的 CLI 不被支持？** UsageBar 不维护工具白名单，等 ccusage 支持后即自动计入，无需更新本应用。

## 更新与卸载

- **更新**：下载新版 Release 替换 /Applications 里的 app 即可。
- **卸载**：面板 → 退出，删除 UsageBar.app；建议卸载前先关掉「开机自启」。装过可选增强的话再删 `~/.local/bin/usage`；不再需要 ccusage 的话顺带 `brew uninstall ccusage`（或 `npm uninstall -g ccusage`）。
- **Windows 更新**：重新下载 exe 覆盖，或重跑 `windows\install.ps1`。
- **Windows 卸载**：完整安装在 **设置 → 应用 → UsageBar → 卸载**（同时移除快捷方式与开机自启；设置保留在 `%APPDATA%\UsageBar`）。便携版直接删 exe 和两个 `.lnk`，再到面板菜单关掉「开机自启」。

## 从源码构建

macOS：

```bash
APP_NAME=UsageBar BUNDLE_ID=com.cookie.UsageBar MENU_BAR_APP=1 SIGNING_MODE=adhoc \
  ARCHES="arm64 x86_64" ./Scripts/package_app.sh release
```

Swift Package（SwiftUI + Charts），核心就一个文件：`Sources/UsageBar/main.swift`。

Windows：见 [Windows 版](#windows-版) 一节，`powershell -File windows\build.ps1` 一条命令，无需 SDK。

## License

MIT — see [LICENSE](LICENSE). ccusage 有其自己的许可条款。

---

## English quick start

1. `brew install --cask zquickm/tap/usagebar` — recommended, no Gatekeeper prompt (the cask strips the quarantine stamp after install).
2. Or download **`UsageBar-v*.dmg`** from [Releases](../../releases), open it, drag **UsageBar** onto the **Applications** folder shown in the window (a Gatekeeper hint is printed right in the install window). One-time prompt (ad-hoc signed): on macOS 15+ double-click once, choose Done, then **System Settings → Privacy & Security → Open Anyway**; on macOS 14 right-click → **Open**.
3. If the data engine [ccusage](https://www.npmjs.com/package/ccusage) isn't installed yet, the popover shows a setup card — click **Install via Homebrew** (or npm) and wait a few minutes. Fully local — ccusage reads each CLI's own session logs, nothing leaves your machine.

Optional terminal extra: `./install.sh` from a repo clone puts a `usage` CLI in `~/.local/bin` (`usage` = 7-day table, `usage 30`, `usage --bar`). The app itself never needs it.
