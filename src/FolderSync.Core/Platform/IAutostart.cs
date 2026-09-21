namespace FolderSync.Core.Platform
{
    /// <summary>开机自启抽象。
    /// Windows: 计划任务 schtasks ONLOGON /rl HIGHEST（manifest 提权语义）；
    /// Linux: ~/.config/autostart/ XDG desktop 项；macOS: ~/Library/LaunchAgents/ plist。</summary>
    public interface IAutostart
    {
        void Enable(bool startMinimized = true);
        void Disable();
        bool IsEnabled();
        /// <summary>自启命令是否带 --minimized（未启用时返回推荐值 true）。</summary>
        bool IsMinimized();
        /// <summary>exe 移位后刷新自启项内的旧路径（设置页打开时调用）。</summary>
        void EnsureUpToDate();
        /// <summary>改写 --minimized 参数（未启用时不创建，留待下次 Enable 生效）。</summary>
        void SetMinimized(bool minimized);
    }
}
