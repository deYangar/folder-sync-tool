using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Windows
{
    /// <summary>Windows Shell 图标/缩略图提供者（二期增强，2026-09-16 移植 WPF 版 MainWindow.xaml.cs 管线）：
    /// 内容缩略图走 IShellItemImageFactory（SIIGBF_THUMBNAILONLY，无缩略图的类型保持类型图标）；
    /// exe/dll/lnk 按路径 SHGetFileInfoW 取真实内嵌图标；其余扩展名按假文件名取关联图标（每扩展名一次）；
    /// 文件夹取系统文件夹图标。产出统一 PNG 字节，HBITMAP/hIcon 即取即毁。
    /// 缓存纪律沿 WPF 版：路径级缓存带上限防涨，null 也缓存避免对无缩略图类型反复探测。</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class ShellIcons : IIconProvider
    {
        private const int PathCacheLimit = 4096;

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFOW
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, uint flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const uint SHGFI_ICON = 0x100, SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint SIIGBF_THUMBNAILONLY = 0x8;   // 0x8！0x10 是 INCACHEONLY（首查必 E_FAIL，缩略图从未出过）

        /// <summary>exe/dll 这类按路径取自身内嵌图标的扩展名（各文件图标不同，不可按扩展名共享）。</summary>
        private static readonly HashSet<string> PerPathIconExt = new(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".dll", ".lnk", ".msi", ".cpl", ".scr", ".sys", ".ocx" };

        private readonly object _gate = new();
        private readonly Dictionary<string, bool> _thumbExtOk = new(StringComparer.OrdinalIgnoreCase);   // 扩展名→有无内容缩略图
        private readonly Dictionary<string, byte[]> _thumbCache = new(StringComparer.OrdinalIgnoreCase); // 路径→缩略图 PNG
        private readonly Dictionary<string, byte[]> _iconCache = new(StringComparer.OrdinalIgnoreCase);  // 路径→图标 PNG（exe/dll/lnk）
        private readonly Dictionary<string, byte[]> _extIconCache = new(StringComparer.OrdinalIgnoreCase);// 扩展名→关联图标 PNG
        private byte[]? _folderPng;

        public string? GetIconKey(string path, bool isDirectory) => null;   // 键由 App 层自绘集管，Shell 通道不参与

        public byte[]? GetSystemIcon(string path, bool isDirectory, int pixelSize)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_gate)
            {
                return isDirectory ? FolderIconPng() : ForFile(path, pixelSize);
            }
        }

        private byte[]? ForFile(string path, int pixelSize)
        {
            var ext = Path.GetExtension(path);
            // 1) 内容缩略图：扩展名首次探测，有缩略图能力的类型逐文件取
            if (ext.Length > 0)
            {
                if (!_thumbExtOk.TryGetValue(ext, out var hasThumb))
                {
                    hasThumb = TryThumbnail(path, pixelSize, out var probed);
                    _thumbExtOk[ext] = hasThumb;
                    if (hasThumb) { _thumbCache[path] = probed!; return probed; }
                }
                else if (hasThumb)
                {
                    if (_thumbCache.TryGetValue(path, out var cached)) return cached;
                    if (TryThumbnail(path, pixelSize, out var png)) { Trim(_thumbCache, path, png!); return png; }
                }
            }
            // 2) 图标层：exe/dll/lnk 按路径（内嵌图标各异）；其余按扩展名关联图标（共享）
            if (PerPathIconExt.Contains(ext))
            {
                if (_iconCache.TryGetValue(path, out var cached)) return cached;
                var png = IconToPng(path, 0);
                Trim(_iconCache, path, png);
                return png;
            }
            if (_extIconCache.TryGetValue(ext, out var extPng)) return extPng;
            extPng = IconToPng("x" + ext, FILE_ATTRIBUTE_NORMAL, useFileAttributes: true);
            Trim(_extIconCache, ext, extPng);
            return extPng;
        }

        private bool TryThumbnail(string path, int pixelSize, out byte[]? png)
        {
            png = null;
            try
            {
                var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0)
                    return false;
                if (factory.GetImage(new SIZE { cx = pixelSize, cy = pixelSize }, SIIGBF_THUMBNAILONLY, out var hbm) != 0
                    || hbm == IntPtr.Zero)
                    return false;
                try { png = HBitmapToPng(hbm); }
                finally { DeleteObject(hbm); }
                return png != null;
            }
            catch { return false; }
        }

        private byte[]? IconToPng(string pathOrFakeName, uint fileAttributes, bool useFileAttributes = false)
        {
            try
            {
                var fi = default(SHFILEINFOW);
                var flags = SHGFI_ICON | (useFileAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
                var ok = SHGetFileInfoW(pathOrFakeName, fileAttributes, ref fi,
                    (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);
                if (ok == IntPtr.Zero || fi.hIcon == IntPtr.Zero) return null;
                try
                {
                    using var icon = Icon.FromHandle(fi.hIcon);
                    using var bmp = icon.ToBitmap();
                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
                finally { DestroyIcon(fi.hIcon); }
            }
            catch { return null; }
        }

        private byte[]? FolderIconPng()
        {
            if (_folderPng != null) return _folderPng;
            var png = IconToPng("x", FILE_ATTRIBUTE_DIRECTORY, useFileAttributes: true);
            _folderPng = png;
            return png;
        }

        private static byte[]? HBitmapToPng(IntPtr hbm)
        {
            try
            {
                using var bmp = Image.FromHbitmap(hbm);
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            catch { return null; }
        }

        /// <summary>路径级缓存上限纪律（WPF 版 PathCacheLimit 同语义）：涨到上限整体清空重建。</summary>
        private void Trim<TVal>(Dictionary<string, TVal> cache, string key, TVal? value)
        {
            if (value == null) return;   // null 不缓存：文件内容/关联可能变化，下次再试
            if (cache.Count >= PathCacheLimit) cache.Clear();
            cache[key] = value;
        }
    }
}
