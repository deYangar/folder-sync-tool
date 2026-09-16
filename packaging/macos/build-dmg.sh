#!/usr/bin/env bash
# FolderSync macOS .app + ad-hoc 签名 + dmg（方案 §7.1：osx-x64/osx-arm64 双架构）
# 用法: build-dmg.sh <版本> <osx-arm64|osx-x64> <dist 输出目录/>
set -euo pipefail

VERSION="$1"; RID="$2"; OUT="$3"
APP_NAME="FolderSync.app"
DMG="FolderSync_${VERSION}_${RID}.dmg"
WORK="$(mktemp -d)"

echo "== publish $RID =="
# 保持隐式 restore（--no-restore 会绕开 ExcludeAssets 评估，Unix 后端 dll 混入产物，2026-09-16 实测）
dotnet publish src/FolderSync.App -c Release -r "$RID" --self-contained -v q -o "$WORK/publish"

echo "== 组 .app =="
APP="$WORK/$APP_NAME"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -r "$WORK/publish/." "$APP/Contents/MacOS/"
cp src/FolderSync.App/assets/app.icns "$APP/Contents/Resources/" 2>/dev/null || true

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>FolderSync</string>
    <key>CFBundleDisplayName</key><string>FolderSync</string>
    <key>CFBundleIdentifier</key><string>com.foldersync.app</string>
    <key>CFBundleVersion</key><string>${VERSION#v}</string>
    <key>CFBundleShortVersionString</key><string>${VERSION#v}</string>
    <key>CFBundleExecutable</key><string>FolderSync</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

echo "== ad-hoc 签名（Gatekeeper：右键打开可用，方案风险表）==="
codesign --force --deep --sign - "$APP"

echo "== dmg =="
mkdir -p "$OUT"
hdiutil create -volname "FolderSync" -srcfolder "$APP" -ov -format UDZO "$OUT/$DMG" -quiet
echo "built: $OUT/$DMG"
rm -rf "$WORK"
