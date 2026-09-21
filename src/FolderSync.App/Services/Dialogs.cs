using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FolderSync.App.Services;

/// <summary>自建消息框（Avalonia 无内置 MessageBox 等价物，且模态对话框是异步的）：
/// Info/Alert/Confirm 三形态，全部 async。静默测试链路（E2eSilent）在调用方短路。</summary>
internal static class Dialogs
{
    public enum Kind { Info, Warn, Error, Question }

    public static Task InfoAsync(Window? owner, string text, string title) => ShowAsync(owner, text, title, Kind.Info);
    public static Task AlertAsync(Window? owner, string text, string title) => ShowAsync(owner, text, title, Kind.Error);

    /// <summary>true=是 / false=否。</summary>
    public static Task<bool> ConfirmAsync(Window? owner, string text, string title) => ShowAsync(owner, text, title, Kind.Question);

    private static async Task<bool> ShowAsync(Window? owner, string text, string title, Kind kind)
    {
        var result = false;
        var dlg = new Window
        {
            Title = title,
            MinWidth = 360, MaxWidth = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            ShowActivated = !E2eSilent.Enabled,
        };

        var glyph = kind switch { Kind.Error => "✖", Kind.Warn => "⚠", Kind.Question => "?", _ => "ℹ" };
        var glyphColor = kind switch
        {
            Kind.Error => (IBrush)new SolidColorBrush(Color.Parse("#C42B1C")),
            Kind.Warn => (IBrush)new SolidColorBrush(Color.Parse("#D29400")),
            _ => (IBrush)new SolidColorBrush(Color.Parse("#0078D4")),
        };

        Button MakeBtn(string caption, bool value)
        {
            var b = new Button { Content = caption, MinWidth = 88, Padding = new Avalonia.Thickness(12, 7) };
            if (value && kind == Kind.Question) b.Classes.Add("primary");
            b.Click += (_, _) => { result = value; dlg.Close(); };
            return b;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        if (kind == Kind.Question)
        {
            buttons.Children.Add(MakeBtn("否", false));
            buttons.Children.Add(MakeBtn("是", true));
        }
        else
        {
            buttons.Children.Add(MakeBtn("确定", true));
        }

        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18, 14, 18, 16),
            Spacing = 14,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = glyph, FontSize = 22, Foreground = glyphColor, VerticalAlignment = VerticalAlignment.Top },
                        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, MaxWidth = 500 },
                    } },
                buttons,
            }
        };

        if (owner != null) await dlg.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            dlg.Closed += (_, _) => tcs.TrySetResult();
            dlg.Show();
            await tcs.Task;
        }
        return result;
    }
}
