using System;
using System.Threading;
using System.Windows;
using FolderSync.Core;

namespace FolderSync.App
{
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private static MainWindow? _main;
        private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? _tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            ThemeManager.Init();   // D3：主题最先应用（先于任何窗口创建，避免浅深闪切）

            // CLI 无头模式（v1.6 B4）先于一切：不建 GUI、不挂托盘、不走单例互斥（与 GUI 实例并存）
            if (Cli.TryRun(e.Args, out var cliExit))
            {
                Shutdown(cliExit);
                return;
            }

            _mutex = new Mutex(true, "FolderSync_SingleInstance", out var isNew);
            if (!isNew)
            {
                MessageBox.Show("FolderSync 已在运行。", "FolderSync", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // 全局异常兜底：弹窗 + 写日志，绝不无声闪退
            DispatcherUnhandledException += (_, args) =>
            {
                WriteCrashLog(args.Exception);
                MessageBox.Show($"发生错误：\n{args.Exception.Message}\n\n详细信息已写入程序目录 crash.log",
                    "FolderSync", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            // 非 UI 线程兜底：工作线程未观察异常至少留全证据（弹窗尽力而为，进程可能仍会终止）
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                WriteCrashLog(e.ExceptionObject);
                try
                {
                    MessageBox.Show($"发生严重错误：\n{e.ExceptionObject}\n\n详细信息已写入程序目录 crash.log",
                        "FolderSync", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { /* 进程正在终止时弹窗可能失败 */ }
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteCrashLog(e.Exception);
                e.SetObserved();   // 不让后台任务异常静默升级为进程崩溃
            };

            base.OnStartup(e);

            // 显式创建主窗口（App.xaml 不设 StartupUri：那个时机在 OnStartup 返回之后，
            // 托盘双击/菜单事件曾因 MainWindow 引用未就绪拿到 null，恢复主窗口永远无效）
            _main = new MainWindow();
            MainWindow = _main;
            if (E2eSilent.Enabled)
                E2eSilentDialogExtensions.InstallForegroundGuard(_main);
            if (!HasArg(e.Args, "--minimized"))
            {
                // 静默测试：窗口照常显示（UIA/截图可用）但不激活，零焦点抢夺
                if (E2eSilent.Enabled) _main.ShowActivated = false;
                _main.Show();
            }
            else
                _main.Hide();

            _tray = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon
            {
                ToolTipText = "FolderSync — 本地文件夹同步"
            };
            System.Windows.Media.Imaging.BitmapSource? baseIcon = null;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                if (icon != null)
                {
                    // Icon → ImageSource
                    var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle,
                        Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    _tray.IconSource = bs;
                    baseIcon = bs;   // 托盘动态图标的底图（同步中叠旋转圆弧）
                }
            }
            catch { /* 图标失败不致命 */ }
            _tray.TrayMouseDoubleClick += (_, _) => _main?.ShowFromTray();
            _tray.ContextMenu = BuildTrayMenu();
            _main.AttachTray(_tray, baseIcon);
        }

        private System.Windows.Controls.ContextMenu BuildTrayMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu();
            var open = new System.Windows.Controls.MenuItem { Header = "打开主窗口" };
            open.Click += (_, _) => _main?.ShowFromTray();
            var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
            exit.Click += (_, _) => { _tray?.Dispose(); _main?.ForceExit(); Shutdown(); };
            menu.Items.Add(open);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(exit);
            return menu;
        }

        private static bool HasArg(string[] args, string arg) =>
            args != null && Array.Exists(args, a => a.Equals(arg, StringComparison.OrdinalIgnoreCase));

        /// <summary>崩溃日志（带轮转：超 1MB 时旧档改 crash.log.old，防长年累月写爆磁盘）。</summary>
        private static void WriteCrashLog(object exception)
        {
            try
            {
                var log = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
                try
                {
                    var fi = new System.IO.FileInfo(log);
                    if (fi.Exists && fi.Length > 1_000_000)
                    {
                        var old = log + ".old";
                        try { if (System.IO.File.Exists(old)) System.IO.File.Delete(old); } catch { }
                        System.IO.File.Move(log, old);
                    }
                }
                catch { /* 轮转失败不阻断写入 */ }
                System.IO.File.AppendAllText(log,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _main?.DisposeTrayIndicator();   // 先停动画轮询，再释放托盘
            _tray?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
