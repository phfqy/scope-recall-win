using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ScopeRecallWin.Controls;

public enum BadgeState { Ok, Warn, Danger, Info }

/// <summary>状态徽章：按 BadgeState 切换语义色（Success/Warning/Danger/Info 系）。</summary>
public sealed partial class StatBadge : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatBadge),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(BadgeState), typeof(StatBadge),
            new PropertyMetadata(BadgeState.Info, OnStateChanged));

    public StatBadge() => InitializeComponent();

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public BadgeState State
    {
        get => (BadgeState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatBadge)d).BadgeText.Text = (string?)e.NewValue ?? string.Empty;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatBadge)d).ApplyState((BadgeState)e.NewValue);

    private void ApplyState(BadgeState state)
    {
        // 语义色与语义色软背景（ColorsAndBrushes.xaml）
        (Brush text, Brush bg) = state switch
        {
            BadgeState.Ok     => (App.Current.Resources["SuccessBrush"] as Brush, App.Current.Resources["SuccessSoftBrush"] as Brush),
            BadgeState.Warn   => (App.Current.Resources["WarningBrush"] as Brush, App.Current.Resources["WarningSoftBrush"] as Brush),
            BadgeState.Danger => (App.Current.Resources["DangerBrush"] as Brush, App.Current.Resources["DangerSoftBrush"] as Brush),
            _                 => (App.Current.Resources["InfoBrush"] as Brush, App.Current.Resources["InfoSoftBrush"] as Brush),
        };
        BadgeText.Foreground = text;
        RootBorder.Background = bg;
    }
}
