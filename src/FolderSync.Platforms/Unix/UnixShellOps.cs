using System.Diagnostics;
using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Unix
{
    /// <summary>Unix Shell 操作：Linux xdg-open / macOS open。</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public sealed class UnixShellOps : IShellOps
    {
        public void OpenPath(string path)
        {
            var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo
            {
                FileName = opener,
                Arguments = path,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
    }
}
