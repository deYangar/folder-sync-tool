using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Windows
{
    /// <summary>Windows Shell 操作：explorer 打开（UseShellExecute 走系统关联）。</summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsShellOps : IShellOps
    {
        public void OpenPath(string path) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            { UseShellExecute = true });
    }
}
