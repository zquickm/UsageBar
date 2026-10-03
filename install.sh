#!/usr/bin/env bash
# UsageBar installer — sets up the data helper (`~/.local/bin/usage`) and its
# only dependency, ccusage. Run from the repo root or anywhere inside it.
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)/bin/usage"
DEST_DIR="$HOME/.local/bin"
mkdir -p "$DEST_DIR"
cp "$SRC" "$DEST_DIR/usage"
chmod +x "$DEST_DIR/usage"
echo "✓ usage helper → $DEST_DIR/usage"

if ! command -v ccusage >/dev/null 2>&1; then
  echo "• ccusage 未安装，尝试 npm install -g ccusage …"
  if command -v npm >/dev/null 2>&1; then
    npm install -g ccusage
  else
    echo "✗ 未找到 npm。请先安装 Node (https://nodejs.org) 或 brew install node，再重跑本脚本。"
    exit 1
  fi
fi
echo "✓ ccusage $(ccusage --version) 就绪"
echo
echo "完成。打开 UsageBar.app，菜单栏闪电图标几秒后即显示今日 token 用量。"
