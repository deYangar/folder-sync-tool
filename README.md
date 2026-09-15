# FolderSync

跨平台文件夹同步工具——单向/双向/镜像同步、块级去重版本库、块级增量传输，桌面 GUI + CLI 双形态。

## 支持平台

| 平台 | x86 | x64 | arm | arm64 |
|---|---|---|---|---|
| Windows | ✅ | ✅ | — | ✅ |
| Linux | CLI | ✅ | ✅ | ✅ |
| macOS | — | ✅ | — | ✅ |

> 当前发布版本为 Windows 桌面版；Linux/macOS 多平台版在 `多平台重写` 分支开发中。

## 功能特性

- **同步模式**：单向、双向、镜像；差异预览逐行改向/跳过；冲突可生成冲突副本
- **传输**：块级增量（CDC 分块 + 内容寻址），大文件改动只传差异块；并行复制（2-4 worker）
- **版本库**：块级去重的文件历史版本（SHA-256 内容寻址 + Brotli 压缩），可浏览/恢复历史版本
- **触发**：手动、定时计划、实时监控（Windows: NTFS USN Journal；Linux: inotify；macOS: FSEvents）
- **安全**：删除进回收站/版本库、跨卷回退、大小写冲突检测、断点取消语义、磁盘满防御
- **GUI**：任务管理、对照树视图、运行历史、深/浅色主题、系统托盘常驻

## CLI

```bash
FolderSync --list                # 列出所有任务
FolderSync --run <任务名>        # 立即执行一个任务
FolderSync --run-all             # 执行全部任务
FolderSync --analyze <任务名>    # 只分析差异，不落盘
```

## 构建与测试

```bash
dotnet build FolderSync.sln -c Release

# 测试套件（C# 自路由框架，无第三方依赖）
cd tests/FolderSync.SmokeTest
dotnet run -c Release            # 默认 smoke 套件
dotnet run -c Release -- twoway  # 指定套件：twoway/delta/versionrepo/move/...
```

持续集成由 GitHub Actions 驱动（见 `.github/workflows/`）。

## 数据目录

- Windows：`%LOCALAPPDATA%\FolderSync`
- Linux：`~/.local/share/FolderSync`
- macOS：`~/Library/Application Support/FolderSync`

可设环境变量 `FOLDERSYNC_DATA` 覆盖。
