using System.Diagnostics;
using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Windows
{
    /// <summary>
    /// Windows 开机自启：计划任务（schtasks ONLOGON /rl HIGHEST）。自 Core/Autostart 平移（静态改实例）。
    /// 为什么不用 HKCU Run 键：程序 manifest 是 requireAdministrator（USN Journal 需要管理员，
    /// 2026-09-12 实测非提权目录句柄 FSCTL_READ_USN_JOURNAL 被拒 win32=5，免提权路线不通），
    /// Run 键由非提权的 explorer 顺序启动，默认 UAC 配置下提权程序会被静默跳过——注册表值在、
    /// 设置页显示"开"，重启后无进程。计划任务以 RunLevel=HIGHEST 由任务计划服务启动，
    /// 不经交互式 UAC 提示链路，登录即起（H-5）。
    /// 注意：注册 HIGHEST 任务本身需要提权——本程序正好是提权运行的，创建动作在自身进程内完成。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsAutostart : IAutostart
    {
        private const string TaskName = "FolderSync";
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string LegacyRunValue = "FolderSync";

        // schtasks 错误输出按控制台 OEM 代码页（中文系统 936）；不注册 CodePages provider 时
        // .NET 默认按 UTF-8 解码，中文错误消息会乱码
        private static readonly System.Text.Encoding ConsoleEncoding = GetConsoleEncoding();

        private static System.Text.Encoding GetConsoleEncoding()
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding(
                    System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch { return System.Text.Encoding.UTF8; }
        }

        /// <summary>schtasks 绝对路径（W-4）：裸文件名走 CreateProcess 搜索序（当前目录在前），
        /// 便携版装在用户可写目录时同名 exe 会以本程序的管理员权限被执行（本地提权面）。</summary>
        private static string SchtasksExe =>
            System.IO.Path.Combine(Environment.SystemDirectory, "schtasks.exe");

        private static (int rc, string stderr) RunSchtasks(string arguments)
        {
            // hideWindow：schtasks 正常无窗口输出；异常时也可能弹控制台窗，CreateNoWindow 兜住
            var psi = new ProcessStartInfo
            {
                FileName = SchtasksExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = ConsoleEncoding
            };
            using var p = Process.Start(psi)!;
            var err = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // schtasks 卡死防御（计划服务无响应等）：30s 放弃；Kill 后仍不让步再等 5s——
            // 旧代码 Kill 失败后裸 WaitForExit() 无限等，UI 线程可能永久挂死（W-4②）
            if (!p.WaitForExit(30_000)) try { p.Kill(); } catch { }
            if (!p.WaitForExit(5_000)) return (-1, "schtasks 超时未退出");
            return (p.ExitCode, err.ToString().Trim());
        }

        /// <summary>查询任务定义 XML（退出码 0 时返回文本；查找参数/路径用 ASCII 子串，不受控制台本地化编码影响）。
        /// 异步读 + 5s 超时 + Kill：同步 ReadToEnd 在 schtasks 挂死时永久卡死设置窗口（RunSchtasks 同款防御）。</summary>
        private static string? QueryXml()
        {
            var psi = new ProcessStartInfo
            {
                FileName = SchtasksExe,
                Arguments = $"/query /tn {TaskName} /xml",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.Unicode
            };
            using var p = Process.Start(psi)!;
            var xml = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) xml.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            if (!p.WaitForExit(5_000)) try { p.Kill(); } catch { }
            if (!p.WaitForExit(5_000)) return null;
            return p.ExitCode == 0 ? xml.ToString() : null;
        }

        /// <summary>启用：创建/覆盖计划任务（当前 exe 路径 + 最小化参数），并清掉旧版本的 HKCU Run 残留。</summary>
        public void Enable(bool startMinimized = true)
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            // /tr 经典转义法：exe 两侧的引号写成 \"，整体再被外层引号包裹。
            // CommandLineToArgvW 在引号模式内把 \" 解析为字面引号、空格不分词，带空格的
            // 路径也能整段传入。2026-09-13 修复：旧写法 Replace 的是路径里的引号字符
            // （路径中本无引号），实际效果是外层引号未转义，带空格路径解析碎裂成多个参数，
            // schtasks 报 E_FAIL(-2147467259)（开发机路径无空格，未暴露）。
            var tr = "\\\"" + exe + "\\\"" + (startMinimized ? " --minimized" : "");
            var (rc, err) = RunSchtasks($"/create /f /tn {TaskName} /tr {Quote(tr)} /sc ONLOGON /rl HIGHEST /it");
            if (rc != 0)
                throw new System.InvalidOperationException($"创建计划任务失败 (schtasks 退出码 {rc}): {err}");
            // 旧版本升级路径：清 HKCU Run 残留（值在但启动不了，还会误导诊断）
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key?.GetValue(LegacyRunValue) != null) key.DeleteValue(LegacyRunValue);
            }
            catch { }
        }

        private static string Quote(string s) => "\"" + s + "\"";

        public void Disable()
        {
            var (rc, err) = RunSchtasks($"/delete /f /tn {TaskName}");
            // 任务不存在（从未创建成功/已被外部删除）视为已关闭，不报错；
            // 只有任务仍在才把失败抛出去
            if (rc != 0 && IsEnabled())
                throw new System.InvalidOperationException($"删除计划任务失败 (schtasks 退出码 {rc}): {err}");
        }

        public bool IsEnabled() => RunSchtasks($"/query /tn {TaskName}").rc == 0;

        /// <summary>自启命令是否带 --minimized（自启未开启时无意义，按 true 回显推荐值）。</summary>
        public bool IsMinimized()
        {
            var xml = QueryXml();
            if (xml == null) return true;
            return xml.Contains("--minimized", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>exe 移位后自启任务里的旧路径失效：路径不一致时按当前 exe 重建（保持最小化设置）。
        /// 设置页打开时调用，顺带把 Run 键残留清掉。</summary>
        public void EnsureUpToDate()
        {
            if (!IsEnabled()) return;
            var xml = QueryXml();
            var exe = Environment.ProcessPath;
            if (xml != null && exe != null && !xml.Contains(exe, System.StringComparison.OrdinalIgnoreCase))
                Enable(IsMinimized());
        }

        /// <summary>改写现有自启命令的 --minimized 参数（自启未开启时不建任务，勾选状态留待下次 Enable 生效）。</summary>
        public void SetMinimized(bool minimized)
        {
            if (!IsEnabled()) return;
            Enable(minimized);
        }
    }
}
