# UsageBar

macOS 菜单栏 token 用量显示。点 ⚡ 弹出面板：7 天平滑曲线 + 精确用量列表，数据来自 `~/.local/bin/usage --chart 7`。

**显示规则**：默认只有"总量"；任何工具/模型仅在你于下拉框（☰）勾选后才出现在曲线和列表里，两处完全联动；"恢复默认"一键回到仅总量。

- 按工具 / 按模型两种模式切换
- 每个系列可自定义颜色（点列表里的色块，系统色轮/RGB/十六进制）
- 悬停曲线查看对应日期的各系列用量
- 60 秒自动刷新

## 构建

```bash
APP_NAME=UsageBar BUNDLE_ID=com.cookie.UsageBar MENU_BAR_APP=1 SIGNING_MODE=adhoc ARCHES=$(uname -m) \
  ./Scripts/package_app.sh release
```

产物为 `UsageBar.app`（菜单栏应用，LSUIElement），依赖你的 `usage` 脚本输出如下 JSON：

```json
{ "days": [ { "date": "2026-10-03", "agents": {"zcode": 123}, "models": {"GLM-5.3": 123} } ], "total": 123456 }
```

Swift Package 结构，单文件实现：`Sources/UsageBar/main.swift`。
