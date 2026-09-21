# tests/e2e — 活的工具

review-20260912 P-14 分类清理后保留的 E2E 工具集（一次性诊断脚本已删，日志/截图不再入库）。
路径基准：脚本位于 `tests/e2e/`，工程根为 `../..`。

## 脚本清单

| 脚本 | 用途 |
|---|---|
| `run_elevated.py` | 通用提权运行器：`python tests/e2e/run_elevated.py <目标脚本绝对路径> <日志绝对路径> [K=V ...]`（K=V 写进 bat 传给提权进程） |
| `run_usn_direct.py` | USN 提权套件直跑已编译的 SmokeTest Release exe（流式输出+7min 硬超时）。经 run_elevated.py 调用；先 `dotnet build -c Release` |
| `e2e_gui.py` | E2E：新建任务全流程（须管理员运行） |
| `e2e_silent_nofocus.py` | E2E：静默模式零焦点抢夺验证（FS_E2E_SILENT=1 + `--e2e` 启动参数双因子） |
| `screenshot.py` | PrintWindow 截图 FolderSync 对话框与主窗口 |
| `check_elevated.py` | 验证 FolderSync.App 进程是否提权运行（TokenElevation） |
| `check_recycle.ps1` | 回收站删除结果验证 |
| `uia_e2e.ps1` | 端到端 UIA 测试：新建任务全流程（须以管理员运行） |
| `usn_probe_noelevated.py` | H-5 探针（2026-09-12）：实测非提权进程读 USN Journal 的能力。结论：QUERY 对目录句柄可行、READ 被拒 win32=5——免提权路线不通，自启走计划任务方案 |

## 静默模式（双因子）

破坏性确认框全自动放行需要**同时**满足：环境变量 `FS_E2E_SILENT=1` **且**以 `--e2e` 参数启动。
只设环境变量无效（2026-09-12 拍板，防随二进制分发的后门风险）。

## C# 测试套件

`tests/FolderSync.SmokeTest`（路由见 Program.cs）：`(无参)` smoke / `usn`（须提权）/ `twoway` /
`fixreg` / `versionrepo` / `delta` / `cancel` / `safety`（review-20260912 修复配套）/ `xvolume` / `diag` / `real`。
USN 套件在非提权 shell 会因卷句柄被拒自动降级 FSW——完整验证用 run_usn_direct.py 提权跑。
