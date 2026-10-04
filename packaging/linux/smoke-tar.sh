#!/usr/bin/env bash
# tar.gz 分发包冒烟：解包 → 注入任务 → 包内 CLI --run 真复制 → 磁盘内容断言。
# 在与包同架构的 runner 上跑（linux-x64 包在 x64 runner、arm64 包在 ARM runner）——
# 交叉场景跑不了 apphost，只能文件级检查，勿把本脚本用在异架构包上。
# 用法: smoke-tar.sh <tar.gz 路径> <解包目录> <可执行文件相对路径>
set -euo pipefail

TAR_FILE="$1"; DIR="$2"; EXE="$3"

mkdir -p "$DIR"
tar -xzf "$TAR_FILE" -C "$DIR"
test -f "$DIR/$EXE" || { echo "::error::$TAR_FILE 包内无 $EXE"; exit 1; }

ROOT="$(mktemp -d)"
mkdir -p "$ROOT/data" "$ROOT/L" "$ROOT/R"
export FOLDERSYNC_DATA="$ROOT/data"
echo "smoke-seed-content" > "$ROOT/L/a.txt"

"$DIR/$EXE" --list | grep -q "共 0 个任务"
"$DIR/$EXE" --list >/dev/null   # 触发库创建
sqlite3 "$ROOT/data/foldersync.db" "INSERT INTO jobs(name,left_path,right_path,direction,created_at) VALUES('smoke','$ROOT/L','$ROOT/R',0,datetime('now'))"
"$DIR/$EXE" --run smoke
grep -q "smoke-seed-content" "$ROOT/R/a.txt"
echo "$TAR_FILE: 包内 CLI --run 真复制通过"

rm -rf "$ROOT" "$DIR"
