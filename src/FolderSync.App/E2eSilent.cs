using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace FolderSync.App
{
    /// <summary>
    /// E2E 静默模式（双因子：环境变量 FS_E2E_SILENT=1 且以 --e2e 命令行参数启动）：
    /// 确认框自动答「是」、提示/错误框不弹（结果写状态行）。
    /// 供自动化测试全程零弹窗零焦点抢夺地跑 GUI 流程；正常用户运行不满足任一因子，行为完全不变。
    /// 双因子把"破坏性确认框全自动放行"的攻击门槛从设个环境变量提高到同时控制环境变量与启动命令行
    /// （随二进制分发时的风险收敛），E2E 提权脚本/bat 只需在启动命令补一个 --e2e 参数。
    /// </summary>
    internal static class E2eSilent
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("FS_E2E_SILENT") == "1"
            && Array.Exists(Environment.GetCommandLineArgs(),
                a => a.Equals("--e2e", StringComparison.OrdinalIgnoreCase));

        public static MessageBoxResult Confirm(Window? owner, string text, string title,
            MessageBoxImage icon = MessageBoxImage.Warning)
            => Enabled ? MessageBoxResult.Yes : MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, icon);

        public static void Info(Window? owner, string text, string title)
        {
            if (!Enabled) MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public static void Alert(Window? owner, string text, string title)
        {
            if (!Enabled) MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 静默测试的零焦点保障：对话框 ShowActivated=false；UIA invoke 会顺带激活 owner——
    /// 前台守卫持续跟踪最近的外部前台窗口，本进程窗口一抢到前台就立即归还。
    /// </summary>
    internal static class E2eSilentDialogExtensions
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private static IntPtr _lastForeign;
        private static bool _guardBusy;

        private static bool IsOwnProcessWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hWnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }

        public static void InstallForegroundGuard(Window main)
        {
            var watcher = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            watcher.Tick += (_, _) =>
            {
                var fg = GetForegroundWindow();
                if (!IsOwnProcessWindow(fg)) _lastForeign = fg;
            };
            watcher.Start();

            main.Activated += (_, _) =>
            {
                if (_guardBusy) return;
                _guardBusy = true;
                int ticks = 0;
                var dt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                dt.Tick += (_, _) =>
                {
                    if (!IsOwnProcessWindow(GetForegroundWindow()) || ++ticks >= 12)
                    {
                        dt.Stop();
                        _guardBusy = false;
                        return;
                    }
                    if (_lastForeign != IntPtr.Zero && !IsOwnProcessWindow(_lastForeign))
                        SetForegroundWindow(_lastForeign);
                };
                dt.Start();
            };
        }

        /// <summary>静默测试下窗口显示但不激活（对 Show/ShowDialog 均生效）。</summary>
        public static void NoActivate(this Window w)
        {
            if (E2eSilent.Enabled) w.ShowActivated = false;
        }

        public static bool? NoActivateThenShowDialog(this Window w)
        {
            if (!E2eSilent.Enabled) return w.ShowDialog();
            w.ShowActivated = false;
            return w.ShowDialog();
        }
    }
}
