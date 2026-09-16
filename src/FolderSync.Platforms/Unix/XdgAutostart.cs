using System;
using System.IO;
using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Unix
{
    /// <summary>Linux 开机自启：XDG autostart desktop 项（~/.config/autostart/FolderSync.desktop，
    /// $XDG_CONFIG_HOME 覆盖）。GNOME/KDE/XFCE 桌面会话启动器均认该目录；
    /// 无桌面会话（纯 headless）时该机制不存在——文档写明，属平台限制。</summary>
    [SupportedOSPlatform("linux")]
    public sealed class XdgAutostart : IAutostart
    {
        private const string FileName = "FolderSync.desktop";

        private static string DesktopFile()
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var dir = string.IsNullOrWhiteSpace(cfg)
                ? Path.Combine(Home(), ".config", "autostart")
                : Path.Combine(cfg, "autostart");
            return Path.Combine(dir, FileName);
        }

        private static string Home() =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>desktop 规范 Exec 的引号转义：路径含空格整体加引号，内部引号反斜杠转义。</summary>
        private static string ExecValue(string exe, bool minimized)
        {
            var quoted = exe.Contains(' ') ? "\"" + exe.Replace("\"", "\\\"") + "\"" : exe;
            return quoted + (minimized ? " --minimized" : "");
        }

        private static void WriteDesktop(string exe, bool minimized)
        {
            var file = DesktopFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, new[]
            {
                "[Desktop Entry]",
                "Type=Application",
                "Name=FolderSync",
                "Comment=本地文件夹同步",
                $"Exec={ExecValue(exe, minimized)}",
                "X-GNOME-Autostart-enabled=true",
                "Terminal=false"
            });
        }

        public void Enable(bool startMinimized = true)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
            WriteDesktop(exe, startMinimized);
        }

        public void Disable()
        {
            var file = DesktopFile();
            if (File.Exists(file)) File.Delete(file);
        }

        public bool IsEnabled() => File.Exists(DesktopFile());

        public bool IsMinimized()
        {
            try { return File.ReadAllText(DesktopFile()).Contains("--minimized", StringComparison.Ordinal); }
            catch { return true; }
        }

        public void EnsureUpToDate()
        {
            if (!IsEnabled()) return;
            var exe = Environment.ProcessPath;
            try
            {
                if (exe != null && !File.ReadAllText(DesktopFile()).Contains(exe, StringComparison.Ordinal))
                    WriteDesktop(exe, IsMinimized());
            }
            catch { /* 读取失败留待下次 */ }
        }

        public void SetMinimized(bool minimized)
        {
            if (!IsEnabled()) return;
            var exe = Environment.ProcessPath;
            if (exe != null) WriteDesktop(exe, minimized);
        }
    }
}
