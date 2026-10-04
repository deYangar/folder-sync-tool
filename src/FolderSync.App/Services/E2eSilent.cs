using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace FolderSync.App.Services;

/// <summary>E2E 静默测试钩子（平移 WPF 版）：FS_E2E_SILENT=1 时窗口 ShowActivated=false
/// （显示但不激活、零焦点抢夺），确认弹窗自动应答——UIA/截图链路不弹真实对话框。</summary>
internal static class E2eSilent
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("FS_E2E_SILENT") == "1";

    /// <summary>静默模式下自动按「是」；正常模式弹真对话框。</summary>
    public static Task<bool> Confirm(Window? owner, string text, string title) =>
        Enabled ? Task.FromResult(true) : Dialogs.ConfirmAsync(owner, text, title);

    public static Task Info(Window? owner, string text, string title) =>
        Enabled ? Task.CompletedTask : Dialogs.InfoAsync(owner, text, title);

    public static Task Alert(Window? owner, string text, string title) =>
        Enabled ? Task.CompletedTask : Dialogs.AlertAsync(owner, text, title);
}
