using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FolderSync.App.Services;
using FolderSync.App.ViewModels;
using FolderSync.Core;

namespace FolderSync.App.Views;

public partial class SettingsWindow : Window
{
    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public async void BtnClearBaseline_Click(object? sender, RoutedEventArgs e)
    {
        var total = BaselineStore.CountAllEntries();
        if (total == 0)
        {
            await E2eSilent.Info(this, "当前没有可清理的基线索引。", "清理基线索引");
            return;
        }
        if (!await E2eSilent.Confirm(this,
            $"确定清理全部任务的块级增量基线索引？\n共 {total} 条（约 {Executor.FormatSize(total * 88L)} 索引量）。\n（不影响两侧文件内容；下次同步大文件时自动重建）",
            "清理基线索引")) return;
        await Vm.ClearBaselineAsync(() => Task.FromResult(true));
        await E2eSilent.Info(this, "基线索引已清理。", "清理基线索引");
    }

    public void Close_Click(object? sender, RoutedEventArgs e) => Close();

    public async void BtnResetLayout_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow main)
            main.ResetWindowLayout();   // 清记录 + 立即回默认尺寸/居中/默认列宽
        else
            AppSettings.ClearWindowLayout();
        await E2eSilent.Info(this, "已还原窗口位置、大小和列宽为默认布局。", "还原窗口布局");
    }
}
