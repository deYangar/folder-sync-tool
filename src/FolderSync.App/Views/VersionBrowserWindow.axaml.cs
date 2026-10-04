using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FolderSync.App.Services;
using FolderSync.Core;
using FolderSync.App.ViewModels;

namespace FolderSync.App.Views;

public partial class VersionBrowserWindow : Window
{
    private VersionBrowserViewModel Vm => (VersionBrowserViewModel)DataContext!;

    public VersionBrowserWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Title = Vm.WindowTitle;
    }

    public void BtnRefresh_Click(object? sender, RoutedEventArgs e) => _ = Vm.ReloadAsync();

    public async void LstFiles_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        await Vm.OnFileSelectedAsync();

    public async void LstFiles_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        // 双击文件：最新版本直接还原
        if (Vm.SelectedFile is not { } f) return;
        var versions = VersionStore.ListVersions(Vm.CurrentSide, Vm.EngineJobId, f.RelPath);
        if (versions.Count == 0) return;
        var latest = System.Linq.Enumerable.OrderByDescending(versions, v => v.Ts)
            .ThenByDescending(v => v.Id).First();
        await Vm.RestoreAsync(this, f.RelPath, latest.Id, Executor.FormatSize(latest.Size));
    }

    public async void LstVersions_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => await RestoreSelectedAsync();

    private async Task RestoreSelectedAsync()
    {
        if (Vm.Selection is not { } sel) return;
        await Vm.RestoreAsync(this, sel.relPath, sel.version.Id, sel.version.SizeText);
    }

    public async void BtnRestore_Click(object? sender, RoutedEventArgs e) => await RestoreSelectedAsync();

    public async void BtnSaveAs_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm.Selection is not { } sel) return;
        var side = Vm.CurrentSide;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "另存版本到…",
            AllowMultiple = false,
        });
        if (picked.Count == 0) return;
        var dir = picked[0].TryGetLocalPath();
        if (dir == null) return;
        var dest = Path.Combine(dir, Path.GetFileName(sel.relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(dest) && !await E2eSilent.Confirm(this, $"目标已存在：\n{dest}\n覆盖？", "另存到")) return;
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
                VersionStore.RestoreVersion(Vm.CurrentSide, sel.version.Id, dest));
        }
        catch (System.Exception ex)
        {
            await E2eSilent.Alert(this, $"另存失败：{ex.Message}", "版本库");
            return;
        }
        await E2eSilent.Info(this, "已另存: " + dest, "版本库");
    }

    public async void BtnDelete_Click(object? sender, RoutedEventArgs e) => await Vm.DeleteVersionAsync(this);

    public async void BtnClearAll_Click(object? sender, RoutedEventArgs e) => await Vm.ClearAllAsync(this);

    public void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
