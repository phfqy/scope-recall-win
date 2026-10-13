using System;
using System.Collections.Generic;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ScopeRecallWin;

public sealed partial class MainWindow : Window
{
    /// <summary>路由 Tag → 视图类型（18 个视图，对应提示词包 P1）。</summary>
    private static readonly Dictionary<string, Type> Views = new()
    {
        ["overview"]    = typeof(Views.OverviewView),
        ["claims"]      = typeof(Views.ClaimsView),
        ["timeline"]    = typeof(Views.TimelineView),
        ["query"]       = typeof(Views.QueryView),
        ["profile"]     = typeof(Views.ProfileView),
        ["queue"]       = typeof(Views.QueueView),
        ["doctor"]      = typeof(Views.DoctorView),
        ["ops"]         = typeof(Views.OpsView),
        ["mutate"]      = typeof(Views.MutateView),
        ["audit"]       = typeof(Views.AuditView),
        ["search"]      = typeof(Views.SearchView),
        ["ai-info"]     = typeof(Views.AiInfoView),
        ["ai-changes"]  = typeof(Views.AiChangesView),
        ["ai-settings"] = typeof(Views.AiSettingsView),
        ["update"]      = typeof(Views.UpdateView),
        ["install"]     = typeof(Views.InstallView),
        ["shared"]      = typeof(Views.SharedStoreView),
        ["settings"]    = typeof(Views.SettingsView),
    };

    public MainWindow()
    {
        InitializeComponent();

        // Mica 背景（Fluent）：Windows 11 生效；旧系统回退纯色（try/catch 兜底）
        TrySetMica();

        // 启动落在总览，入场动画（EntranceNavigationTransitionInfo：从底部上滑，参考 MS Learn Motion）
        NavView.SelectedItem = NavOverview;
        ContentFrame.Navigate(typeof(Views.OverviewView), null,
            new EntranceNavigationTransitionInfo());

        // ⌘K 键盘加速器：Ctrl+K 唤起命令面板（P1 补全过滤/执行逻辑）
        var accelerator = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.K,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        accelerator.Invoked += (_, _) => TogglePalette();
        RootGrid.KeyboardAccelerators.Add(accelerator);

        // 关闭窗口最小化到托盘：由 Services/TrayService 在 P4 接入（此处预留）
    }

    private void TrySetMica()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        catch
        {
            // 不支持的平台：保留纯色 Background，不中断启动
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem { Tag: string tag }
            && Views.TryGetValue(tag, out var viewType))
        {
            // 同级视图切换用水平滑动（Page transitions 指南）；进入深层用 Drill
            ContentFrame.Navigate(viewType, null,
                new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
        }
    }

    private void TogglePalette()
    {
        PaletteOverlay.Visibility =
            PaletteOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (PaletteOverlay.Visibility == Visibility.Visible)
        {
            PaletteInput.Focus(FocusState.Programmatic);
        }
    }
}
