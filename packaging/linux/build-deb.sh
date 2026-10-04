#!/usr/bin/env bash
# FolderSync deb 打包（方案 §7.1：amd64/arm64 双架构，dpkg-deb 交叉打包，无需真机）
# 用法: build-deb.sh <版本> <架构 amd64|arm64> <publish 输出目录> <dist 输出目录> [发布说明文件]
#   发布说明文件（可选）：每行一条更新项，「- 」开头成列表项，其余行为段落；进入
#   metainfo 的 <release><description>，软件中心「发布版本详细信息」展示，空缺则显示未提供
set -euo pipefail

VERSION="$1"; DEB_ARCH="$2"; SRC="$3"; OUT="$4"; NOTES_FILE="${5:-}"
NAME="FolderSync_${VERSION}_linux-${DEB_ARCH}.deb"
PKG="$(mktemp -d)/pkg"
BUILD_DATE="$(date +%F)"
REL_TYPE=stable; [[ "$VERSION" == *-* ]] && REL_TYPE=development
REPO="$(cd "$(dirname "$0")/../.." && pwd)"

# 目录结构：/usr/bin/foldersync（publish 全量）+ 桌面项 + hicolor 图标 + AppStream 元数据
install -d "$PKG/usr/bin" "$PKG/usr/share/applications" \
  "$PKG/usr/share/icons/hicolor/256x256/apps" "$PKG/usr/share/metainfo" "$PKG/DEBIAN"
cp -r "$SRC/." "$PKG/usr/bin/"
mv "$PKG/usr/bin/FolderSync" "$PKG/usr/bin/foldersync" 2>/dev/null || true
chmod +x "$PKG/usr/bin/foldersync" 2>/dev/null || true

# 软件中心/应用菜单图标（Icon=foldersync 的实体，缺了显示灰白占位）
install -m 644 "$REPO/src/FolderSync.App/assets/app.png" \
  "$PKG/usr/share/icons/hicolor/256x256/apps/foldersync.png"

cat > "$PKG/usr/share/applications/foldersync.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=FolderSync
Comment=本地文件夹同步
Exec=foldersync
Icon=foldersync
Terminal=false
Categories=Utility;FileTools;
Keywords=folder;sync;backup;文件夹;同步;备份;
StartupWMClass=foldersync
EOF

# AppStream 元数据：中文完整描述放这里（装好后经系统 AppStream 缓存展示）。
# Ubuntu 新版 App Center（flutter 版）会把 control Description 里的非 ASCII 显示成
# "？"（alpha.2 解包实锤 control 中文完好、纯显示端 bug），所以 control 只留 ASCII；
# developer_name / project_license / release date 分别补软件中心的
# 「未知发布者 / 许可证 unknown / 发布于 Unknown」三空。
# 发布说明：notes 文件行转 XML 段落/列表（&<> 转义；容忍 CRLF），空缺则自闭合 release。
esc_xml() { sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }
REL_DESC=""
if [ -n "$NOTES_FILE" ] && [ -f "$NOTES_FILE" ]; then
  in_ul=0
  while IFS= read -r note_line || [ -n "$note_line" ]; do
    note_line="${note_line%$'\r'}"
    [ -z "$note_line" ] && continue
    case "$note_line" in
      "- "*)
        [ "$in_ul" -eq 0 ] && { REL_DESC="${REL_DESC}<ul>"; in_ul=1; }
        REL_DESC="${REL_DESC}<li>$(printf '%s' "${note_line#- }" | esc_xml)</li>" ;;
      *)
        [ "$in_ul" -eq 1 ] && { REL_DESC="${REL_DESC}</ul>"; in_ul=0; }
        REL_DESC="${REL_DESC}<p>$(printf '%s' "$note_line" | esc_xml)</p>" ;;
    esac
  done < "$NOTES_FILE"
  [ "$in_ul" -eq 1 ] && REL_DESC="${REL_DESC}</ul>"
fi
if [ -n "$REL_DESC" ]; then
  REL_BLOCK="<release version=\"${VERSION#v}\" date=\"${BUILD_DATE}\" type=\"${REL_TYPE}\">
    <description>${REL_DESC}</description>
  </release>"
else
  REL_BLOCK="<release version=\"${VERSION#v}\" date=\"${BUILD_DATE}\" type=\"${REL_TYPE}\"/>"
fi

cat > "$PKG/usr/share/metainfo/foldersync.metainfo.xml" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>foldersync</id>
  <metadata_license>CC0-1.0</metadata_license>
  <project_license>LicenseRef-Proprietary</project_license>
  <name>FolderSync</name>
  <summary>本地文件夹同步：块级增量传输、内容寻址版本库</summary>
  <description>
    <p>FolderSync 是一款本地文件夹同步工具，提供单向、双向、镜像、备份四种同步模式。</p>
    <p>块级增量传输与内容寻址版本库让大文件的改动只传差异；支持实时监控、定时与计划（cron）触发。</p>
    <p>本包为自包含 .NET 构建，除 GUI 渲染库外无额外运行时依赖。</p>
  </description>
  <launchable type="desktop-id">foldersync.desktop</launchable>
  <developer_name>FolderSync</developer_name>
  <provides>
    <binary>foldersync</binary>
  </provides>
  <releases>
    ${REL_BLOCK}
  </releases>
  <content_rating type="oars-1.1" />
</component>
EOF

# 依赖：.NET 运行时自包含；libicu 由 SkiaSharp 运行期按需（B-2：alternatives 链补 Debian 13 的
# libicu76 与 Ubuntu 26 的 libicu78，新版系统缺名会导致 dpkg 直接拒装）；
# GUI 运行库（libfontconfig1 字体枚举 + X11/libxkbcommon 键盘映射）显式声明——最小系统上
# dpkg 不拦但运行时才炸缺库起不来，声明后装包时即补齐
cat > "$PKG/DEBIAN/control" <<EOF
Package: foldersync
Version: ${VERSION#v}
Section: utils
Priority: optional
Architecture: ${DEB_ARCH}
Depends: libicu70 | libicu72 | libicu74 | libicu76 | libicu78, libfontconfig1, libx11-6, libxkbcommon0, libxkbcommon-x11-0
Homepage: https://github.com/deYangar/folder-sync-tool
Maintainer: FolderSync <noreply@foldersync.local>
Description: FolderSync - local folder sync with block-level delta transfer
 One-way/two-way/mirror/backup sync modes; block-level delta transfer with a
 content-addressed version store; real-time, scheduled and cron triggers.
 Self-contained .NET build - no runtime deps beyond GUI rendering libraries.
EOF

dpkg-deb --build --root-owner-group "$PKG" "$OUT/$NAME"
echo "built: $OUT/$NAME"
rm -rf "$(dirname "$PKG")"
