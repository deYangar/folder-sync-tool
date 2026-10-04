using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FolderSync.App.ViewModels;

namespace FolderSync.App.Views;

public partial class FailedItemsWindow : Window
{
    private FailedItemsViewModel Vm => (FailedItemsViewModel)DataContext!;

    public FailedItemsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Title = Vm.Title;
    }

    public async void BtnRetry_Click(object? sender, RoutedEventArgs e)
    {
        var btn = (Button)sender!;
        btn.IsEnabled = false;
        try
        {
            await Vm.RetryAsync(this, busy => { });
        }
        finally { btn.IsEnabled = true; }
    }

    public void BtnLog_Click(object? sender, RoutedEventArgs e)
    {
        var logFile = Vm.Engine.LastFailedLogFile;
        if (logFile != null && File.Exists(logFile))
            Core.Platform.Platform.Shell.OpenPath(logFile);
    }

    public void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
