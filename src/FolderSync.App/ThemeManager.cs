using System;
using System.Windows;
using Microsoft.Win32;

namespace FolderSync.App
{
    /// <summary>主题管理（v1.7 D3）：三档（跟随系统/浅色/深色），设置存 HKCU，
    /// 切换=运行时换合并字典（XAML DynamicResource 全自动刷新；代码侧静态画刷缓存随事件重载）。</summary>
    internal static class ThemeManager
    {
        /// <summary>主题档位：0=跟随系统（默认），1=浅色，2=深色。</summary>
        public static int ThemeMode
        {
            get => AppSettings.ThemeMode;
            set
            {
                AppSettings.ThemeMode = value;
                Apply();
            }
        }

        private static volatile int _applied = -1;   // 已应用档（-1 未初始化）

        /// <summary>启动时应用（App.OnStartup 最早调用，先于任何窗口创建）。</summary>
        public static void Init()
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && AppSettings.ThemeMode == 0)
                    Apply();   // 跟随系统档：系统深浅变化即时切换
            };
            Apply();
        }

        /// <summary>按档位换合并字典（幂等：同档不重复换）。
        /// UserPreferenceChanged 回调在系统广播线程触发——改 Application 资源必须封送 UI 线程，
        /// 否则与 UI 线程的 DynamicResource 查找竞态。</summary>
        public static void Apply()
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(Apply);
                return;
            }
            var effective = EffectiveTheme();   // 0=light 1=dark
            if (effective == _applied) return;
            _applied = effective;
            var uri = new Uri($"Themes/{(effective == 1 ? "Dark" : "Light")}.xaml", UriKind.Relative);
            var dict = new ResourceDictionary { Source = uri };
            var md = Application.Current.Resources.MergedDictionaries;
            // 主题字典固定占第一个槽位（其后是 App.xaml 自身的样式字典——顺序保证样式里的 DynamicResource 可解析）
            if (md.Count > 0 && md[0].Source?.OriginalString.Contains("Themes/") == true)
                md[0] = dict;
            else
                md.Insert(0, dict);
            ThemeBrushes.Reload();
            ThemeChanged?.Invoke();
        }

        /// <summary>生效主题（0=浅 1=深）：跟随系统档读 AppsUseLightTheme（缺省视为浅色）。</summary>
        private static int EffectiveTheme()
        {
            switch (AppSettings.ThemeMode)
            {
                case 1: return 0;
                case 2: return 1;
                default:
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        return k?.GetValue("AppsUseLightTheme") is int v && v == 0 ? 1 : 0;
            }
        }

        /// <summary>主题切换通知（代码侧画刷缓存重载后，树/列表刷新绑定用）。</summary>
        public static event Action? ThemeChanged;
    }

    /// <summary>主题画刷缓存（代码侧取色统一入口）：从当前资源字典取 Freeze 画刷并缓存，
    /// ThemeManager.Apply 时 Reload。键缺失回退浅色值再回退 Gray，绝不抛（缺键只影响观感）。</summary>
    internal static class ThemeBrushes
    {
        private static System.Windows.Media.Brush[] _cache = Array.Empty<System.Windows.Media.Brush>();

        internal enum K
        {
            WindowBg, CardBg, CardBorder, Separator, HeaderBg, HoverBg, TextPrimary, TextSecondary, TextTertiary,
            Accent, AccentDark, AccentForeground, LeftBand, LeftBandText, RightBand, RightBandText,
            Success, SuccessBg, Danger, DangerBg, Warning, WarningDeep, WarningBg, Teal, LogBg, LogFg,
            TreeAltBg, RowHoverBg, ConflictRowBg, ProgressBg, PanelBg, PanelBg2, InputBg,
            BadgeGrayBg, BadgeGrayFg, StatusIdle, StatusRun, StatusWarn, StatusDisabled, StatusMedia,
            IconFolder, IconDoc, StepDone, VersionBadge
        }

        private static readonly string[] Names =
        {
            "WindowBg", "CardBg", "CardBorder", "Separator", "HeaderBg", "HoverBg", "TextPrimary", "TextSecondary", "TextTertiary",
            "Accent", "AccentDark", "AccentForeground", "LeftBand", "LeftBandText", "RightBand", "RightBandText",
            "Success", "SuccessBg", "Danger", "DangerBg", "Warning", "WarningDeep", "WarningBg", "Teal", "LogBg", "LogFg",
            "TreeAltBg", "RowHoverBg", "ConflictRowBg", "ProgressBg", "PanelBg", "PanelBg2", "InputBg",
            "BadgeGrayBg", "BadgeGrayFg", "StatusIdle", "StatusRun", "StatusWarn", "StatusDisabled", "StatusMedia",
            "IconFolder", "IconDoc", "StepDone", "VersionBadge"
        };

        public static System.Windows.Media.Brush C(K k)
        {
            var i = (int)k;
            if (_cache.Length == 0) Reload();
            return i < _cache.Length && _cache[i] != null
                ? _cache[i]
                : System.Windows.Media.Brushes.Gray;
        }

        public static void Reload()
        {
            var arr = new System.Windows.Media.Brush[Names.Length];
            for (int i = 0; i < Names.Length; i++)
            {
                if (Application.Current?.TryFindResource(Names[i]) is System.Windows.Media.Brush b)
                    arr[i] = b;
            }
            _cache = arr;
        }
    }
}
