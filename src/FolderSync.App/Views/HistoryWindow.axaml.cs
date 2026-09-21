using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FolderSync.App.Services;
using FolderSync.Core;
using FolderSync.App.ViewModels;

namespace FolderSync.App.Views;

public partial class HistoryWindow : Window
{
    private HistoryViewModel Vm => (HistoryViewModel)DataContext!;

    public HistoryWindow()
    {
        InitializeComponent();
        // 调用方经对象初始化器赋 DataContext（构造先跑）：构造期 Vm 必为 null，
        // 标题/订阅移入 Opened 与另两个子窗口（VersionBrowser/FailedItems）同款纪律
        Opened += (_, _) =>
        {
            Title = $"运行历史 — {Vm.Engine.Job.Name}";
            Vm.Engine.RunFinished += OnRunFinished;
        };
    }

    private void OnRunFinished(SyncEngine engine, RunRecord r) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Vm.Reload());

    protected override void OnClosed(EventArgs e)
    {
        // DataContext 赋值前就关闭（极端时序）时 Vm 为 null，退订守卫
        if (DataContext is HistoryViewModel) Vm.Engine.RunFinished -= OnRunFinished;
        base.OnClosed(e);
    }

    public void OpenReport_Click(object? sender, RoutedEventArgs e) => Vm.OpenReport();
    public void OpenFailed_Click(object? sender, RoutedEventArgs e) => Vm.OpenFailed();

    public async void ExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出运行历史",
            SuggestedFileName = $"history-{Vm.Engine.Job.Name}-{System.DateTime.Now:yyyyMMdd-HHmmss}",
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } } }
        });
        if (file == null) return;
        var path = file.TryGetLocalPath();
        if (path == null) return;
        try
        {
            Vm.ExportCsv(path);
            await E2eSilent.Info(this, "已导出: " + path, "导出");
        }
        catch (System.Exception ex)
        {
            await E2eSilent.Alert(this, "导出失败: " + ex.Message, "错误");
        }
    }

    public void Refresh_Click(object? sender, RoutedEventArgs e) => Vm.Reload();
    public void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
