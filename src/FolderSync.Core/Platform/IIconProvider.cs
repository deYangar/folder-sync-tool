using System.IO;

namespace FolderSync.Core.Platform
{
    /// <summary>树/文件图标提供者（M2 Avalonia 图标体系用）：
    /// 内嵌矢量扩展名大类图标为主，Windows 真实关联图标列为二期增强。</summary>
    public interface IIconProvider
    {
        /// <summary>按路径取图标资源键（内嵌矢量图标集的键名；null=未知类型用默认文件图标）。</summary>
        string? GetIconKey(string path, bool isDirectory);

        /// <summary>系统缩略图/关联图标（2026-09-16 二期增强提前落地）：
        /// Windows=Shell 管线（IShellItemImageFactory 内容缩略图 → SHGetFileInfoW 关联/exe 真实图标）；
        /// Linux=freedesktop 缩略图缓存 + 图片文件直读；macOS=图片文件直读（QuickLook 后续）。
        /// 返回常见图片格式字节（PNG 优先，Avalonia Bitmap 可解码）；null=平台未实现/未命中，调用方回退自绘。
        /// 同步阻塞调用（含 P/Invoke 与磁盘 IO），调用方必须自行后台化。pixelSize=期望正方形边长 px。</summary>
        byte[]? GetSystemIcon(string path, bool isDirectory, int pixelSize);
    }
}
