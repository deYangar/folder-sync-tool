using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FolderSync.App.Services;
using FolderSync.App.ViewModels;

namespace FolderSync.App.Views;

/// <summary>任务编辑窗口：浏览选目录（StorageProvider 跨平台文件夹选择器）+ 确定/取消。</summary>
public partial class JobEditWindow : Window
{
    /// <summary>对话框结果（ShowDialog 调用方读取）。</summary>
    public bool Result { get; private set; }

    private JobEditViewModel Vm => (JobEditViewModel)DataContext!;

    public JobEditWindow()
    {
        InitializeComponent();
    }

    public async void BrowseLeft_Click(object? sender, RoutedEventArgs e) => await BrowseAsync(0);
    public async void BrowseRight_Click(object? sender, RoutedEventArgs e) => await BrowseAsync(1);

    private async Task BrowseAsync(int which)
    {
        var suggest = Vm.LeftPath;
        if (which == 1) suggest = Vm.RightPath;
        var options = new FolderPickerOpenOptions
        {
            Title = which == 0 ? "选择左侧文件夹" : "选择右侧文件夹",
            AllowMultiple = false,
        };
        try
        {
            if (System.IO.Directory.Exists(suggest))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(suggest);
        }
        catch { /* 起始目录尽力而为 */ }
        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        if (picked.Count > 0)
        {
            var path = picked[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (which == 0) Vm.LeftPath = path;
                else Vm.RightPath = path;
            }
        }
    }

    public async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var error = Vm.Validate();
        if (error != null)
        {
            await E2eSilent.Alert(this, error, "无法保存");
            return;
        }
        // D1 保存确认：自动裁决且无任何败者保留手段 → 弹一次确认
        // （G-4：走 E2eSilent 包装——全应用唯一裸弹 Dialogs 的确认框，FS_E2E_SILENT=1 无人值守链路会真弹模态框卡死）
        if (Vm.Direction == FolderSync.Core.SyncDirection.TwoWay
            && Vm.ConflictPolicy is FolderSync.Core.ConflictPolicy.NewestMtime or FolderSync.Core.ConflictPolicy.LargestSize
            && Vm.VersionKeepCount == 0 && !AppSettings.SkipConflictLoseWarn)
        {
            var r = await E2eSilent.Confirm(this,
                "当前组合下（自动裁决 + 版本保留 0 代），冲突败者的内容会被直接丢弃。\n\n" +
                "建议：版本保留设为 N 代，或冲突策略改选「冲突副本 / 手动裁决」。\n\n" +
                "（确认=继续保存并记为不再提示，取消=返回修改）",
                "败者内容将丢失");
            if (!r) return;
            AppSettings.SkipConflictLoseWarn = true;
        }
        Result = true;
        Close();
    }

    public void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
