#!/bin/bash
set -e

# ===== Disk Monitor ビルドスクリプト =====
# Mac: mac/build.sh に委譲
# Windows: Windows PC で windows/build.ps1 を実行

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo "🚀 Disk Monitor — Mac 版をビルドします..."
exec ./mac/build.sh "$@"
