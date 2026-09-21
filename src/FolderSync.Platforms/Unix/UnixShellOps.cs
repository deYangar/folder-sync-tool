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
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = opener,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // S-5：必须 ArgumentList 逐参传递——Arguments 字符串会被 .NET 按命令行规则再切分，
            // 含空格路径（macOS 数据目录 ~/Library/Application Support/... 必中）拆成两个参数 100% 失败；
            // '-' 开头的路径经 ArgumentList 也不会被当选项解析
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 极简发行版无 xdg-open：打开失败不应冒泡炸 UI（调用方多为托盘/报告的顺手打开动作）
        }
    }
    }
}
