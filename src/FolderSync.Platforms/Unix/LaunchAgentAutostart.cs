using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Unix
{
    /// <summary>macOS 开机自启：LaunchAgent（~/Library/LaunchAgents/com.foldersync.app.plist +
    /// launchctl load/unload）。用户级、无需 root，登录即起。</summary>
    [SupportedOSPlatform("macos")]
    public sealed class LaunchAgentAutostart : IAutostart
    {
        private const string Label = "com.foldersync.app";

        private static string PlistFile() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", Label + ".plist");

        private static void WritePlist(string exe, bool minimized)
        {
            var file = PlistFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // XML plist（不依赖 plist 命令行工具）；ProgramArguments 数组天然免引号转义问题。
            // U-3：exe 路径必须 SecurityElement.Escape——含 &（文件系统合法字符）的路径产非法 XML，
            // launchd 拒载自启静默失效，而 IsEnabled 只查文件存在、UI 仍显示已启用
            var esc = System.Security.SecurityElement.Escape(exe) ?? exe;
            var lines = new List<string>
            {
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
                "<plist version=\"1.0\">",
                "<dict>",
                "    <key>Label</key>",
                $"    <string>{Label}</string>",
                "    <key>ProgramArguments</key>",
                "    <array>",
                $"        <string>{esc}</string>"
            };
            if (minimized) lines.Add("        <string>--minimized</string>");   // 参考#9：不写空行占位
            lines.AddRange(new[] { "    </array>", "    <key>RunAtLoad</key>", "    <true/>", "</dict>", "</plist>" });
            File.WriteAllLines(file, lines);
        }

        /// <summary>launchctl 单动词调用（U-2）：参数经 ArgumentList 逐参传递——
        /// 旧代码把 "unload …; load …" 整串塞 Arguments，posix_spawn 无 shell 解释，
        /// "2&gt;/dev/null;" 与 "load" 被当字面参数，靠 launchctl 容忍多路径侥幸工作，macOS 升级即翻车。</summary>
        private static int LaunchCtl(string verb, string file)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "launchctl",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(verb);
                psi.ArgumentList.Add(file);
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit(10_000)) try { p.Kill(); } catch { }
                if (!p.WaitForExit(5_000)) return -1;
                return p.ExitCode;
            }
            catch { return -1; }
        }

        public void Enable(bool startMinimized = true)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
            WritePlist(exe, startMinimized);
            // 重写 plist 后重载：先 unload 旧定义（未加载时非零退出码，忽略）再 load 新定义
            var file = PlistFile();
            LaunchCtl("unload", file);
            LaunchCtl("load", file);
        }

        public void Disable()
        {
            LaunchCtl("unload", PlistFile());
            if (File.Exists(PlistFile())) File.Delete(PlistFile());
        }

        public bool IsEnabled() => File.Exists(PlistFile());

        public bool IsMinimized()
        {
            try { return File.ReadAllText(PlistFile()).Contains("--minimized", StringComparison.Ordinal); }
            catch { return true; }
        }

        public void EnsureUpToDate()
        {
            if (!IsEnabled()) return;
            var exe = Environment.ProcessPath;
            try
            {
                if (exe != null && !File.ReadAllText(PlistFile()).Contains(exe, StringComparison.Ordinal))
                    WritePlist(exe, IsMinimized());
            }
            catch { }
        }

        public void SetMinimized(bool minimized)
        {
            if (!IsEnabled()) return;
            var exe = Environment.ProcessPath;
            if (exe != null) WritePlist(exe, minimized);
        }
    }
}
