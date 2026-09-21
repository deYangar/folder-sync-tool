#!/usr/bin/env bash
# FolderSync deb 打包（方案 §7.1：amd64/arm64 双架构，dpkg-deb 交叉打包，无需真机）
# 用法: build-deb.sh <版本> <架构 amd64|arm64> <publish 输出目录> <dist 输出目录>
set -euo pipefail

VERSION="$1"; DEB_ARCH="$2"; SRC="$3"; OUT="$4"
NAME="FolderSync_${VERSION}_linux-${DEB_ARCH}.deb"
PKG="$(mktemp -d)/pkg"

# 目录结构：/usr/bin/foldersync（publish 全量）+ 桌面项
install -d "$PKG/usr/bin" "$PKG/usr/share/applications" "$PKG/DEBIAN"
cp -r "$SRC/." "$PKG/usr/bin/"
mv "$PKG/usr/bin/FolderSync" "$PKG/usr/bin/foldersync" 2>/dev/null || true
chmod +x "$PKG/usr/bin/foldersync" 2>/dev/null || true

cat > "$PKG/usr/share/applications/foldersync.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=FolderSync
Comment=本地文件夹同步
Exec=foldersync
Icon=foldersync
Terminal=false
Categories=Utility;FileTools;
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
Maintainer: FolderSync <noreply@foldersync.local>
Description: FolderSync — 本地文件夹同步
 单向/双向/镜像/备份同步；块级增量与块级去重版本库；实时/定时/计划触发。
 本包为自包含 .NET 构建，除 GUI 渲染库外无额外运行时依赖。
EOF

dpkg-deb --build --root-owner-group "$PKG" "$OUT/$NAME"
echo "built: $OUT/$NAME"
rm -rf "$(dirname "$PKG")"
