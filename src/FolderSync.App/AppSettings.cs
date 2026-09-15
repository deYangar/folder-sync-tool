using Microsoft.Win32;

namespace FolderSync.App
{
    /// <summary>软件设置（HKCU\Software\FolderSync，勾选即存）。</summary>
    internal static class AppSettings
    {
        private const string KeyPath = @"Software\FolderSync";

        // 读走静态缓存（L-10）：树构建的文件循环里逐个读注册表是纯浪费；写设置时同步刷新缓存
        private static int _showIcons = int.MinValue;

        /// <summary>对照表显示系统图标与缩略图（默认开）。关闭=文件只用通用图标、不提取缩略图，节省系统资源。</summary>
        public static bool ShowIcons
        {
            get
            {
                if (_showIcons == int.MinValue) _showIcons = ReadInt("ShowIcons", 1);
                return _showIcons != 0;
            }
            set
            {
                _showIcons = value ? 1 : 0;
                WriteInt("ShowIcons", _showIcons);
            }
        }

        private static int _skipConflictLoseWarn = int.MinValue;

        /// <summary>不再提示「自动裁决且无任何败者保留手段」（D1 保存确认，用户勾选后落 HKCU）。</summary>
        public static bool SkipConflictLoseWarn
        {
            get
            {
                if (_skipConflictLoseWarn == int.MinValue) _skipConflictLoseWarn = ReadInt("SkipConflictLoseWarn", 0);
                return _skipConflictLoseWarn != 0;
            }
            set
            {
                _skipConflictLoseWarn = value ? 1 : 0;
                WriteInt("SkipConflictLoseWarn", _skipConflictLoseWarn);
            }
        }

        private static int _themeMode = int.MinValue;

        /// <summary>主题档位（D3）：0=跟随系统（默认）/ 1=浅色 / 2=深色。</summary>
        public static int ThemeMode
        {
            get
            {
                if (_themeMode == int.MinValue) _themeMode = Math.Clamp(ReadInt("ThemeMode", 0), 0, 2);
                return _themeMode;
            }
            set
            {
                _themeMode = Math.Clamp(value, 0, 2);
                WriteInt("ThemeMode", _themeMode);
            }
        }

        private static int ReadInt(string name, int def)
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(name) is int v ? v : def;
        }

        private static void WriteInt(string name, int v)
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            k.SetValue(name, v, RegistryValueKind.DWord);
        }
    }
}
