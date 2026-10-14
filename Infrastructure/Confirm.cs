using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Infrastructure;

/// <summary>
/// 统一的二次确认对话框（替代原先的"手写授权句"）。
///
/// 约定：所有会改动记忆库或本机配置的操作，执行前都必须经此确认，说明三件事：
///   1) 将要做什么与影响范围；2) 不可逆的后果（红字）；3) 需要用户本人点确认。
/// </summary>
public static class Confirm
{
    /// <summary>
    /// 弹出确认框。返回 true 表示用户确认执行。
    /// </summary>
    /// <param name="anchor">任意已加载的元素（用于取 XamlRoot）。</param>
    /// <param name="title">标题（动词开头，如"重新判定候选"）。</param>
    /// <param name="message">将要做什么 + 影响范围（口语化）。</param>
    /// <param name="consequence">不可逆后果说明（红色显示，可为 null）。</param>
    /// <param name="primaryText">确认按钮文字（默认"确认执行"）。</param>
    public static async Task<bool> AskAsync(
        FrameworkElement anchor,
        string title,
        string message,
        string? consequence = null,
        string primaryText = "确认执行")
    {
        var body = new StackPanel { Spacing = 10 };

        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
        });

        if (!string.IsNullOrWhiteSpace(consequence))
        {
            body.Children.Add(new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DangerSoftBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Child = new TextBlock
                {
                    Text = "⚠ " + consequence,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DangerBrush"],
                },
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = "点击下方按钮即表示你本人确认执行本次操作。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"],
        });

        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = anchor.XamlRoot,
        };

        try
        {
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["DangerButtonStyle"];
        }
        catch
        {
            // 样式不可用时用默认按钮外观
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>信息提示框（只有一个"知道了"按钮）。</summary>
    public static async Task InformAsync(FrameworkElement anchor, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
            CloseButtonText = "知道了",
            XamlRoot = anchor.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
