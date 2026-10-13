using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ScopeRecallWin.Controls;

/// <summary>
/// 通用卡片：标题（可选）+ 内容。视觉参考 Files 的紧凑卡片（Surface + 1px 边框 + 8px 圆角）。
/// </summary>
public sealed partial class Card : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(Card),
            new PropertyMetadata(null, OnTitleChanged));

    public Card()
    {
        InitializeComponent();
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (Card)d;
        card.TitleText.Text = e.NewValue as string;
        card.TitleText.Visibility = string.IsNullOrEmpty(e.NewValue as string)
            ? Visibility.Collapsed : Visibility.Visible;
    }
}
