using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace FolderSync.App.Services;

/// <summary>树图标集（方案项 11）：内嵌矢量扩展名大类图标，三平台一致、零系统依赖。
/// Windows 系统关联图标/缩略图列为二期增强（IIconProvider 已预留）。
/// 绘制风格：单色 Glyph 几何 + 类型色，16-20px 视觉下与现版观感同级。</summary>
internal static class TreeIcons
{
    private static readonly Dictionary<string, IImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IImage DefaultDoc = Build("M4 1 L11 1 L14 4 L14 15 L4 15 Z", "#8A929C");
    private static readonly IImage Folder = Build("M1 4 C1 2.9 1.9 2 3 2 L6 2 L8 4 L13 4 C14.1 4 15 4.9 15 6 L15 13 C15 14.1 14.1 15 13 15 L3 15 C1.9 15 1 14.1 1 13 Z", "#E0A82E");

    /// <summary>扩展名 → 大类图标（未知类型回退通用文档）。</summary>
    public static IImage ForExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return DefaultDoc;
        lock (Cache)
        {
            if (Cache.TryGetValue(ext, out var cached)) return cached;
            var img = BuildFor(ext);
            Cache[ext] = img;
            return img;
        }
    }

    public static IImage ForFolder() => Folder;
    public static IImage Default => DefaultDoc;

    private static IImage BuildFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".heic" or ".tiff"
            => Build("M2 3 L9 3 L11 5 L14 5 L14 13 L2 13 Z M5 7 A1.4 1.4 0 1 1 5 9.8 A1.4 1.4 0 0 1 5 7 Z M3 11 L7 8 L9.5 10 L12 7.5 L13 11 Z", "#107C10"),
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v"
            => Build("M2 4 L11 4 L11 12 L2 12 Z M11 7 L14 5 L14 11 L11 9 Z", "#0078D4"),
        ".mp3" or ".flac" or ".wav" or ".ogg" or ".m4a" or ".aac" or ".wma"
            => Build("M6 1.5 L8 1.5 L8 8.6 C8 9.9 7.1 11 5.8 11 C4.5 11 3.5 10 3.5 8.8 C3.5 7.6 4.5 6.7 5.7 6.7 C5.8 6.7 5.9 6.7 6 6.72 Z", "#03898F"),
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso"
            => Build("M3 2 L13 2 L13 14 L3 14 Z M5 4 L11 4 L11 6 L5 6 Z M5 8 L11 8 L11 9 L5 9 Z M5 10 L11 10 L11 11 L5 11 Z", "#B45309"),
        ".cs" or ".py" or ".js" or ".ts" or ".cpp" or ".c" or ".h" or ".java" or ".go" or ".rs" or ".rb" or ".php" or ".sh" or ".ps1"
            => Build("M5.5 3 L10.5 3 L14 8 L10.5 13 L5.5 13 L2 8 Z", "#6D28D9"),
        ".txt" or ".md" or ".log" or ".ini" or ".cfg" or ".conf" or ".yaml" or ".yml" or ".json"
            => Build("M4 1 L11 1 L14 4 L14 15 L4 15 Z M6.5 7 L11.5 7 L11.5 8 L6.5 8 Z M6.5 9.5 L11.5 9.5 L11.5 10.5 L6.5 10.5 Z M6.5 12 L11.5 12 L11.5 13 L6.5 13 Z", "#57606A"),
        ".pdf"
            => Build("M4 1 L11 1 L14 4 L14 15 L4 15 Z M6.5 8 A2.2 2.2 0 1 0 6.5 12.4 L6.5 11 L8.7 11 L8.7 10 L6.5 10 Z", "#C42B1C"),
        ".xls" or ".xlsx" or ".csv" or ".ods"
            => Build("M3 2 L13 2 L13 14 L3 14 Z M5 5 L11 5 L11 6 L5 6 Z M5 7.5 L11 7.5 L11 8.5 L5 8.5 Z M5 10 L11 10 L11 11 L5 11 Z", "#107C10"),
        ".doc" or ".docx" or ".odt" or ".rtf"
            => Build("M3 2 L13 2 L13 14 L3 14 Z M5 5 L8.5 5 L8.5 6 L5 6 Z M5 7.5 L11 7.5 L11 8.5 L5 8.5 Z M5 10 L11 10 L11 11 L5 11 Z", "#0078D4"),
        ".ppt" or ".pptx" or ".odp"
            => Build("M3 2 L13 2 L13 14 L3 14 Z M5 5 L11 5 L11 8 Q8 10.5 5 8 Z", "#D29400"),
        ".exe" or ".dll" or ".so" or ".dylib" or ".app" or ".deb" or ".msi" or ".apk"
            => Build("M8 1 L9.8 5.2 L14 5.2 L10.6 8 L11.9 12.3 L8 9.8 L4.1 12.3 L5.4 8 L2 5.2 L6.2 5.2 Z", "#0078D4"),
        _ => DefaultDoc,
    };

    private static IImage Build(string pathData, string color)
    {
        var geo = StreamGeometry.Parse(pathData);
        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            dc.DrawGeometry(new SolidColorBrush(Color.Parse(color)), null, geo);
        }
        return new DrawingImage(dg);
    }
}
