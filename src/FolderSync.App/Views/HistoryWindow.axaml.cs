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
        Title = $"运行历史 — {Vm.Engine.Job.Name}";
        Vm.Engine.RunFinished += OnRunFinished;
    }

    private void OnRunFinished(SyncEngine engine, RunRecord r) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Vm.Reload());

    protected override void OnClosed(EventArgs e)
    {
        Vm.Engine.RunFinished -= OnRunFinished;
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
