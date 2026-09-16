using Avalonia;
using Avalonia.Styling;

namespace FolderSync.App.Services;

/// <summary>主题服务：三档（跟随系统/浅色/深色）映射 Avalonia RequestedThemeVariant。
/// 「跟随系统」= Default（Avalonia 内置监听系统深浅变化即时切换，替代 WPF 版
/// SystemEvents.UserPreferenceChanged + 注册表 AppsUseLightTheme 的全部职责）；
/// Palette.axaml 的 ThemeDictionaries 让全部 DynamicResource 自动刷新。</summary>
internal static class ThemeService
{
    public static void Init() => Apply();

    public static int ThemeMode
    {
        get => AppSettings.ThemeMode;
        set { AppSettings.ThemeMode = value; Apply(); }
    }

    /// <summary>档位 → RequestedThemeVariant。</summary>
    private static void Apply()
    {
        if (Application.Current == null) return;
        Application.Current.RequestedThemeVariant = AppSettings.ThemeMode switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
