using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ScopeRecallWin.Controls;

public enum LedState { Ok, Warn, Danger, Info, Off }

/// <summary>状态 LED：ok=绿 / warn=黄 / danger=红 / info=蓝 / off=暗。</summary>
public sealed partial class StatusLed : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(LedState), typeof(StatusLed),
            new PropertyMetadata(LedState.Info, OnStateChanged));

    public StatusLed() => InitializeComponent();

    public LedState State
    {
        get => (LedState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var led = (StatusLed)d;
        Brush? brush = (LedState)e.NewValue switch
        {
            LedState.Ok     => App.Current.Resources["SuccessBrush"] as Brush,
            LedState.Warn   => App.Current.Resources["WarningBrush"] as Brush,
            LedState.Danger => App.Current.Resources["DangerBrush"] as Brush,
            LedState.Off    => App.Current.Resources["MutedTextBrush"] as Brush,
            _               => App.Current.Resources["InfoBrush"] as Brush,
        };
        led.LedEllipse.Fill = brush;
    }
}
