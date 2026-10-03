# UsageBar ⚡

macOS menu bar app that tracks your AI CLI token usage (Claude Code, Codex, ZCode, OpenCode, Gemini CLI, Kimi, Qwen … any of the 19 CLIs [ccusage](https://github.com/ccusage/ccusage) supports).

![screenshot](docs/screenshot.png)

One click on the bolt shows a 7-day smoothed chart + exact numbers, per **tool** (zcode / codex / …) or per **model** (GLM-5.3 / gpt-6.1 / kimi / qwen …). Runs fully locally — it only reads the usage logs your CLI tools already write on your disk.

**中文简介**：macOS 菜单栏 AI CLI token 用量统计。点闪电图标查看 7 天曲线和精确数值，支持按工具/按模型两种视角；数据完全本地读取（ccusage 扫描各 CLI 自己的会话日志），无需任何 API key。

## Install

1. Download `UsageBar.app.zip` from [Releases](../../releases), unzip, drag **UsageBar.app** to `/Applications` (or anywhere).
2. **First launch** (unsigned app): right-click the app → **Open** → confirm. macOS will remember it afterwards.
3. Run the installer (sets up the `usage` data helper + [ccusage](https://www.npmjs.com/package/ccusage) dependency):

```bash
git clone https://github.com/zquickm/UsageBar && cd UsageBar && ./install.sh
```

That's it — the menu bar bolt shows today's tokens within a minute.

**安装（中文）**：下载 Release 里的 zip 解压拖进应用程序；首次打开用**右键 → 打开**（应用未签名，属正常提示）；然后跑一次仓库里的 `./install.sh`（装数据脚本和 ccusage 依赖）即可。

## How it works

```
CLI 工具的本地日志 ──► ccusage (聚合, --by-agent) ──► usage 脚本 ──► UsageBar.app
   (~/​.zcode, ~/.codex, ~/.claude …)                    (~/.local/bin)      (菜单栏)
```

- `bin/usage` (installed to `~/.local/bin/usage`) makes **one** aggregate `ccusage daily --by-agent` call, merges the optional `~/.dsh` ledger, and emits JSON.
- The app refreshes every 60 s; per-tool coverage follows whatever your installed ccusage supports (no whitelist to maintain).
- New models appear automatically as you use them; new tools show up the day ccusage detects their logs.

## Features

- 按工具 / 按模型两种模式，7 天平滑曲线 + 当日/7 天精确用量
- 默认只显示总量；任何工具/模型勾选后才出现（下拉框内勾选）
- 用量列表拖拽排序（记忆顺序）、每个系列自定义颜色（点色块开系统色轮）
- 悬停曲线查看对应日期用量；按天虚线网格
- 开机自启（面板内开关，登录项方式）

## Build from source

```bash
APP_NAME=UsageBar BUNDLE_ID=com.cookie.UsageBar MENU_BAR_APP=1 SIGNING_MODE=adhoc \
  ARCHES="arm64 x86_64" ./Scripts/package_app.sh release
```

Swift Package (SwiftUI + Charts), single file: `Sources/UsageBar/main.swift`. macOS 14+.

## Notes & limits

- The app is ad-hoc signed, so Gatekeeper shows the "unidentified developer" dialog once — right-click → Open.
- Cost columns rely on ccusage pricing data; models it doesn't know show `$0`.
- If you use a CLI ccusage can't parse yet, its tokens won't be counted (until ccusage adds support).

## License

MIT — see [LICENSE](LICENSE). ccusage is separately licensed under its own terms.
