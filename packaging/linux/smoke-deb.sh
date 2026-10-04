#!/usr/bin/env bash
# deb 包结构冒烟：control + AppStream metainfo + hicolor 图标必须真实进包。
# 软件中心元数据（简介/发布说明/图标）缺实体就是 alpha.1/alpha.2 的灰白占位与三空回潮。
# 用法: smoke-deb.sh <deb 路径>
set -euo pipefail

DEB="$1"
dpkg-deb -I "$DEB" | grep -q Package
dpkg-deb -c "$DEB" | grep -q "usr/share/metainfo/foldersync.metainfo.xml"
dpkg-deb -c "$DEB" | grep -q "usr/share/icons/hicolor/256x256/apps/foldersync.png"
echo "$(basename "$DEB"): control + metainfo + hicolor 图标就位"
