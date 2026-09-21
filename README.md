# FolderSync

跨平台文件夹同步工具——单向/双向/镜像同步、块级去重版本库、块级增量传输，Avalonia 桌面 GUI + CLI 双形态，Linux / macOS / Windows 三平台。

> 多平台重写版（`多平台重写` 分支）：三平台一套代码（Core 引擎 + Platforms 平台层 + Avalonia UI），全部产物由 GitHub Actions 构建。

## 支持平台

| 平台 | x86 | x64 | arm | arm64 | 产物 |
|---|---|---|---|---|---|
| Windows | ✅ | ✅ | — | ✅ | zip + Setup.exe（管理员权限：USN 实时监控） |
| Linux | CLI-only | ✅ | ✅ | ✅ | tar.gz + deb(amd64/arm64)（普通权限） |
| macOS | — | ✅（Intel） | — | ✅（Apple Silicon） | dmg（普通权限，未签名：首次右键打开） |

> linux-x86 因 SkiaSharp 无原生包出 CLI-only 版（引擎全功能可用，仅无 GUI）。
> .NET 8 最低系统：Windows 10 1607+ / macOS 12+ / Debian 11+；Linux arm 需 armv7+（Pi 2 起）。

## 功能特性

- **同步模式**：单向、双向、镜像、备份；差异预览逐行改向/跳过；冲突可生成冲突副本
- **传输**：块级增量（CDC 分块 + 内容寻址），大文件改动只传差异块；并行复制（2-4 worker）
- **版本库**：块级去重的文件历史版本（SHA-256 内容寻址 + Brotli 压缩），可浏览/恢复历史版本
- **触发**：手动、定时计划、实时监控（Windows: NTFS USN Journal；Linux: inotify；macOS: FSEvents）
- **安全**：删除进回收站（Win 系统回收站 / Linux XDG Trash / macOS ~/.Trash）、跨卷回退、大小写冲突检测、断点取消语义、磁盘满防御
- **GUI**：Avalonia 11 五窗口（任务/编辑/设置/历史/版本库）、对照树视图、深/浅/跟随系统主题、系统托盘常驻

## 平台差异（降级矩阵）

| 能力 | Windows | Linux | macOS |
|---|---|---|---|
| 实时监控 | USN（持久 journal） | inotify（溢出全扫补） | FSEvents（溢出全扫补） |
| 创建时间保留 | ✅ | ❌（ext4 crtime 不可移植） | ✅（birthtime） |
| 网络盘识别 | ✅ | 一期不识别（按普通目录） | 同 Linux |
| 提权 | 管理员（manifest） | 无需 | 无需 |
| 托盘 | ✅ | 桌面环境相关（GNOME 需扩展） | ✅ 菜单栏 |

## CLI

```bash
FolderSync --list                # 列出所有任务
FolderSync --run <任务名>        # 立即执行一个任务
FolderSync --run-all             # 执行全部任务
FolderSync --analyze <任务名>    # 只分析差异，不落盘
# 退出码：0 成功 / 2 用法错 / 3 部分失败 / 4 失败 / 5 任务不存在
```

## 构建与测试

```bash
dotnet build FolderSync.sln -c Release

# 测试套件（C# 自路由框架，无第三方依赖，三平台可跑）
dotnet run --project tests/FolderSync.SmokeTest -c Release            # 默认 smoke 套件
dotnet run --project tests/FolderSync.SmokeTest -c Release -- twoway  # 指定套件
# Windows 上加 -f net8.0-windows（可跑 usn/diskfull 专属套件；非提权自动 SKIP）

# CLI-only 变体（linux-x86）
dotnet publish src/FolderSync.App -c Release -r linux-x86 --self-contained -p:IncludeGUI=false
```

持续集成由 GitHub Actions 驱动：

- **push/PR** → `.github/workflows/ci.yml`：Windows / Ubuntu / Ubuntu-ARM 真机 / macOS 四 job 各跑全套 SmokeTest（约 5-10 分钟）
- **打 `v*` 标签** → `.github/workflows/release.yml`：9 RID 全量打包（zip/Setup.exe/tar.gz/deb/dmg，SHA-256 校验和）+ 分发包冒烟（包内 CLI 真跑 `--run`）→ GitHub Release **draft**（发布需手动确认）

## 数据目录

- Windows：`%LOCALAPPDATA%\FolderSync`（老版随 exe 目录的数据首启自动迁移）
- Linux：`$XDG_DATA_HOME/FolderSync`，未设则 `~/.local/share/FolderSync`
- macOS：`~/Library/Application Support/FolderSync`

可设环境变量 `FOLDERSYNC_DATA` 覆盖。便携式换机注意：数据不再随 exe 走。
