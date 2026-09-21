using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FolderSync.App.Services;

/// <summary>软件设置：JSON 文件存储（数据目录 settings.json），勾选即存。
/// 跨平台平移 WPF 版（HKCU 注册表）；Windows 老用户首启自动把注册表值搬进 JSON。</summary>
internal static class AppSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private sealed class Bag
    {
        public int ShowIcons { get; set; } = 1;
        public int SkipConflictLoseWarn { get; set; }
        public int ThemeMode { get; set; }
        public int RememberWindow { get; set; } = 1;
        public int WinX { get; set; }
        public int WinY { get; set; }
        public double WinW { get; set; }
        public double WinH { get; set; }
        public int WinMaximized { get; set; }
        public Dictionary<string, double>? ColumnWidths { get; set; }
    }

    private static Bag? _bag;
    private static string FilePath => Path.Combine(Core.Platform.AppPaths.Root, "settings.json");

    private static Bag Load()
    {
        if (_bag != null) return _bag;
        try
        {
            if (File.Exists(FilePath))
                _bag = JsonSerializer.Deserialize<Bag>(File.ReadAllText(FilePath)) ?? new Bag();
        }
        catch
        {
            // G-7：损坏不能静默重置——旧配置无提示永久丢失。留档 .corrupt-<时间> 供用户找回，
            // 本进程按默认值继续（设置项均为展示/行为偏好，默认值不会丢数据）
            try { File.Move(FilePath, $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
            catch { }
        }
        _bag ??= MigrateFromRegistry();
        return _bag;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // G-7：tmp+rename 原子替换——WriteAllText 直写目标，断电/被杀落中途会产出截断 JSON，
            // 下次启动反序列化失败、全部设置静默回默认。tmp 同目录同卷，Move 覆盖原子
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Load(), JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 设置写失败不致命 */ }
    }

    /// <summary>Windows 老版升级路径：HKCU\Software\FolderSync 有值则搬进 JSON（一次性，搬完保留注册表不动——回滚旧版仍可用）。</summary>
    private static Bag MigrateFromRegistry()
    {
        var bag = new Bag();
        if (!OperatingSystem.IsWindows()) return bag;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\FolderSync");
            if (k == null) return bag;
            if (k.GetValue("ShowIcons") is int i1) bag.ShowIcons = i1;
            if (k.GetValue("SkipConflictLoseWarn") is int i2) bag.SkipConflictLoseWarn = i2;
            if (k.GetValue("ThemeMode") is int i3) bag.ThemeMode = i3;
        }
        catch { }
        return bag;
    }

    /// <summary>对照表显示图标（默认开）。跨平台一期为内嵌矢量图标集（系统关联图标是 Windows 二期增强）。</summary>
    public static bool ShowIcons
    {
        get => Load().ShowIcons != 0;
        set { Load().ShowIcons = value ? 1 : 0; Save(); }
    }

    /// <summary>不再提示「自动裁决且无任何败者保留手段」（D1 保存确认）。</summary>
    public static bool SkipConflictLoseWarn
    {
        get => Load().SkipConflictLoseWarn != 0;
        set { Load().SkipConflictLoseWarn = value ? 1 : 0; Save(); }
    }

    /// <summary>主题档位（D3）：0=跟随系统（默认）/ 1=浅色 / 2=深色。</summary>
    public static int ThemeMode
    {
        get => Math.Clamp(Load().ThemeMode, 0, 2);
        set { Load().ThemeMode = Math.Clamp(value, 0, 2); Save(); }
    }

    /// <summary>记住上次窗口位置和大小（默认开）。关闭后不再应用也不再记录，窗口回系统默认。</summary>
    public static bool RememberWindow
    {
        get => Load().RememberWindow != 0;
        set { Load().RememberWindow = value ? 1 : 0; Save(); }
    }

    /// <summary>上次窗口位置和大小。从未记录（WinW&lt;=0）返回 null。</summary>
    public static (int x, int y, double w, double h, bool max)? WindowBounds
    {
        get
        {
            var b = Load();
            return b.WinW > 0 ? (b.WinX, b.WinY, b.WinW, b.WinH, b.WinMaximized != 0) : null;
        }
    }

    /// <summary>记录窗口 bounds。maximized=true 时只更新最大化标志、保留上次正常态 bounds
    /// （还原窗口时的尺寸不该是铺满全屏的那个）。</summary>
    public static void SaveWindowBounds(int x, int y, double w, double h, bool max)
    {
        var b = Load();
        b.WinMaximized = max ? 1 : 0;
        if (!max)
        {
            b.WinX = x; b.WinY = y; b.WinW = w; b.WinH = h;
        }
        Save();
    }

    /// <summary>清除已记录的窗口 bounds（关闭「记住」时调用，下次按系统默认出现）。</summary>
    public static void ClearWindowBounds()
    {
        var b = Load();
        b.WinW = 0; b.WinH = 0; b.WinMaximized = 0;
        Save();
    }

    /// <summary>对照表列宽（列头文本 → 像素宽，全局一份跨任务共用）。列从未拖过则缺项。</summary>
    public static IReadOnlyDictionary<string, double> ColumnWidths =>
        Load().ColumnWidths ?? new Dictionary<string, double>();

    /// <summary>记录单列宽度（拖动结束防抖后调用）。无记录字典时惰性建。</summary>
    public static void SaveColumnWidth(string header, double width)
    {
        var b = Load();
        b.ColumnWidths ??= new Dictionary<string, double>();
        b.ColumnWidths[header] = Math.Max(20, width);
        Save();
    }

    /// <summary>一键还原窗口布局：清掉窗口位置/大小记录和全部列宽记录（设置窗口「还原窗口布局」按钮）。</summary>
    public static void ClearWindowLayout()
    {
        var b = Load();
        b.WinW = 0; b.WinH = 0; b.WinMaximized = 0;
        b.ColumnWidths = null;
        Save();
    }
}
