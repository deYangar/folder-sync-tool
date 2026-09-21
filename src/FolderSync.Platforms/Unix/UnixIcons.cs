using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Unix
{
    /// <summary>Unix（Linux/macOS）图标提供者（二期增强，2026-09-16）：
    /// 图片文件直接读字节（Avalonia/Skia 解码即内容缩略图，与桌面环境无关）；
    /// Linux 附加 freedesktop 缩略图缓存读取（~/.cache/thumbnails/{large,normal}/md5(uri).png，
    /// 与 Nautilus/Dolphin 同源，文件管理器看过的视频/文档即有）。未命中回退 null（App 层用自绘图标）。
    /// ELF 可执行文件无「按路径图标」概念（.desktop 才有），一律回退自绘。
    /// macOS QuickLook（QLThumbnailGenerator）留后续增强。</summary>
    internal sealed class UnixIcons : IIconProvider
    {
        /// <summary>Skia 内置可直接解码的图片类型：读原字节即得内容缩略图。</summary>
        private static readonly HashSet<string> DirectImageExt = new(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".tif" };

        private static readonly HashSet<string> MacDirectImageExt = new(DirectImageExt, StringComparer.OrdinalIgnoreCase)
            { ".heic", ".heif" };   // macOS 专属常见格式（Skia 不一定带解码器，留给 QuickLook 后续；先按原字节给出去）

        private readonly bool _isMac;
        private readonly object _gate = new();

        public UnixIcons(bool isMac) => _isMac = isMac;

        public string? GetIconKey(string path, bool isDirectory) => null;

        public byte[]? GetSystemIcon(string path, bool isDirectory, int pixelSize)
        {
            if (string.IsNullOrEmpty(path) || isDirectory) return null;
            try
            {
                var ext = Path.GetExtension(path);
                if (DirectImageExt.Contains(ext) || (_isMac && MacDirectImageExt.Contains(ext)))
                    return File.ReadAllBytes(path);
                if (!_isMac)
                    return ReadFreeDesktopThumbnail(path, pixelSize);
            }
            catch { /* 文件消失/IO 错误：回退自绘 */ }
            return null;
        }

        /// <summary>freedesktop 缩略图规范（Thumbnail Managing Standard 0.9）：
        /// 缓存名 = MD5("file://" + percent-encoded 绝对路径).png，large=256px / normal=128px。
        /// 参考#7：缓存根尊重 $XDG_CACHE_HOME；子目录档位按请求的 pixelSize 选取（大尺寸请求
        /// 不拿 normal 的 128px 糊图，小尺寸请求不必强制 x-large）。
        /// 严格规范还要求校验 PNG tEXt 里的 Thumb::MTime 与文件 mtime 一致；此处从简只判存在——
        /// 文件变更后缓存由文件管理器/系统刷新，对照表刷新一轮即拿到新图。</summary>
        private static byte[]? ReadFreeDesktopThumbnail(string path, int pixelSize)
        {
            string uri;
            try { uri = new Uri(path).AbsoluteUri; }
            catch { return null; }
            var hex = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(uri))).ToLowerInvariant();
            var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var cacheRoot = Path.Combine(
                string.IsNullOrWhiteSpace(xdgCache)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
                    : xdgCache,
                "thumbnails");
            var sizes = pixelSize >= 200
                ? new[] { "x-large", "large", "normal" }
                : new[] { "normal", "large", "x-large" };
            foreach (var size in sizes)
            {
                var candidate = Path.Combine(cacheRoot, size, hex + ".png");
                if (File.Exists(candidate))
                    return File.ReadAllBytes(candidate);
            }
            return null;
        }
    }
}
