using System;
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
            // XML plist（不依赖 plist 命令行工具）；ProgramArguments 数组天然免引号转义问题
            File.WriteAllLines(file, new[]
            {
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
                "<plist version=\"1.0\">",
                "<dict>",
                "    <key>Label</key>",
                $"    <string>{Label}</string>",
                "    <key>ProgramArguments</key>",
                "    <array>",
                $"        <string>{exe}</string>",
                minimized ? "        <string>--minimized</string>" : "",
                "    </array>",
                "    <key>RunAtLoad</key>",
                "    <true/>",
                "</dict>",
                "</plist>"
            });
        }

        private static int LaunchCtl(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "launchctl",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit(10_000)) try { p.Kill(); } catch { }
                p.WaitForExit();
                return p.ExitCode;
            }
            catch { return -1; }
        }

        public void Enable(bool startMinimized = true)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
            WritePlist(exe, startMinimized);
            LaunchCtl($"unload \"{PlistFile()}\" 2>/dev/null; load \"{PlistFile()}\"");
        }

        public void Disable()
        {
            LaunchCtl($"unload \"{PlistFile()}\"");
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
