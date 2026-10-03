#!/usr/bin/env bash
# UsageBar optional terminal extra — installs the `usage` CLI (daily report in
# Terminal) and its dependency ccusage. The app itself does NOT need this:
# it bundles its own aggregation and offers a one-click ccusage install.
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
echo "完成。终端里试试：usage（近 7 天明细表）、usage 30、usage --bar。"
