using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

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

    /// <summary>导航分组：组名 + （Tag / 标题 / Segoe 图标码 / 悬浮提示=副标题）。</summary>
    private static readonly (string Group, (string Tag, string Label, string Glyph, string Tip)[] Items)[] NavGroups =
    {
        ("总览与洞察", new[]
        {
            ("overview", "总览", "\uE80F", "健康分 / 规模 / 缺口"),
            ("claims",   "记忆", "\uE8A5", "claim 全量 + 状态机"),
            ("timeline", "时间线", "\uE823", "session/episode/capture 事件"),
            ("query",    "查询", "\uE721", "recall / trace / entity"),
            ("profile",  "档案", "\uE77B", "profile 确定性分类视图"),
        }),
        ("运行与诊断", new[]
        {
            ("queue",  "队列", "\uE8FD", "work_items + 失败重试"),
            ("doctor", "诊断", "\uE9D9", "doctor 报告与能力缺口"),
            ("ops",    "运维", "\uE898", "backup/adopt/migrate/respace"),
        }),
        ("操作与审计", new[]
        {
            ("mutate", "变更", "\uE70F", "人工修改记忆（revise / forget + 授权句）"),
            ("audit",  "审计", "\uE81C", "record_event 时间线 + 订阅"),
            ("search", "检索", "\uE71C", "词法关键词检索"),
        }),
        ("AI 与配置", new[]
        {
            ("ai-info",     "AI 模型", "\uE768", "worker 状态 / 今日统计"),
            ("ai-changes",  "AI 改动", "\uE895", "candidate_lifecycle 留痕"),
            ("ai-settings", "AI 设置", "\uE713", "runtime-config 全量编辑器"),
        }),
        ("维护与共享", new[]
        {
            ("update",   "更新", "\uE777", "一键更新插件本体"),
            ("install",  "安装", "\uE8B7", "plan/apply-install 向导"),
            ("shared",   "共享存储", "\uE716", "init-shared / attach / import-entry"),
            ("settings", "设置", "\uE713", "主题 / 巡检 / 开机自启"),
        }),
    };

    /// <summary>命令面板条目（⌘K 数据源）。</summary>
    private sealed record PaletteItem(string Tag, string Label, string Tip, string Group);

    /// <summary>单个导航按钮的各可视部件（选中态切换用）。</summary>
    private sealed class NavButtonParts
    {
        public Button Button = null!;
        public Border Indicator = null!;
        public TextBlock LabelText = null!;
        public TextBlock BadgeText = null!;
    }

    private readonly Dictionary<string, NavButtonParts> _navButtons = new();
    private readonly Dictionary<Type, UIElement> _viewCache = new();
    private readonly List<PaletteItem> _paletteItems = new();
    private NavButtonParts? _activeParts;

    public MainWindow()
    {
        App.Trace("MainWindow: before InitializeComponent");
        InitializeComponent();
        App.Trace("MainWindow: after InitializeComponent");

        App.Trace("MainWindow: before TrySetMica");
        TrySetMica();
        App.Trace("MainWindow: after TrySetMica");

        App.Trace("MainWindow: before BuildNavigation");
        BuildNavigation();
        App.Trace("MainWindow: after BuildNavigation");
        BuildPaletteItems();
        // 启动页：默认总览；可用环境变量 SR_START_PAGE=<tag> 直达某页（自动化验证/快捷入口）
        var startTag = Environment.GetEnvironmentVariable("SR_START_PAGE");
        if (string.IsNullOrWhiteSpace(startTag) || !Views.ContainsKey(startTag))
        {
            startTag = "overview";
        }
        App.Trace($"MainWindow: before NavigateTo({startTag})");
        NavigateTo(Views[startTag]);
        if (_navButtons.TryGetValue(startTag, out var startParts))
        {
            SetActive(startParts);
        }
        App.Trace($"MainWindow: after NavigateTo({startTag})");

        // ⌘K 键盘加速器：Ctrl+K 唤起命令面板
        var accelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.K,
            Modifiers = VirtualKeyModifiers.Control,
        };
        accelerator.Invoked += (_, _) => TogglePalette();
        RootGrid.KeyboardAccelerators.Add(accelerator);
        App.Trace("MainWindow: ctor done");

        // 关闭窗口最小化到托盘：由 Services/TrayService 在 P4 接入（此处预留）
    }

    // ---------- 侧边栏 ----------

    /// <summary>按 NavGroups 生成左导航项（分组标题 + 图标/主标签/副标题/角标按钮）。</summary>
    private void BuildNavigation()
    {
        foreach (var (group, items) in NavGroups)
        {
            NavPanel.Children.Add(new TextBlock
            {
                Text = group,
                Style = TryFindResource("NavGroupHeaderStyle"),
                Margin = new Thickness(10, 12, 10, 4),
            });

            foreach (var (tag, label, glyph, tip) in items)
            {
                if (!Views.TryGetValue(tag, out var viewType))
                {
                    continue;
                }

                var parts = CreateNavButton(tag, label, glyph, tip);
                parts.Button.Click += (_, _) =>
                {
                    NavigateTo(viewType);
                    SetActive(parts);
                };
                NavPanel.Children.Add(parts.Button);
                _navButtons[tag] = parts;

                if (tag == "overview")
                {
                    SetActive(parts);
                }
            }
        }
    }

    /// <summary>
    /// 导航按钮结构：[3px 选中指示条][图标][主标签 + 副标题][状态角标]。
    /// 副标题常显（原 Tip 仅悬浮提示，信息不可见）；角标默认隐藏，
    /// 由 <see cref="SetNavBadge"/> 在有新版本/积压等状态时显示。
    /// </summary>
    private NavButtonParts CreateNavButton(string tag, string label, string glyph, string tip)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 6, 8, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var indicator = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var glyphText = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res<SolidColorBrush>("MutedTextBrush") ?? new SolidColorBrush(Colors.Gray),
        };

        var textStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 13.5,
            Foreground = Res<SolidColorBrush>("TextBrush") ?? new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var tipText = new TextBlock
        {
            Text = tip,
            FontSize = 11,
            Foreground = Res<SolidColorBrush>("MutedTextBrush") ?? new SolidColorBrush(Colors.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        textStack.Children.Add(labelText);
        textStack.Children.Add(tipText);

        var badgeText = new TextBlock
        {
            Text = "",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Res<SolidColorBrush>("AccentBrush") ?? new SolidColorBrush(Colors.LightBlue),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        Grid.SetColumn(indicator, 0);
        Grid.SetColumn(glyphText, 1);
        Grid.SetColumn(textStack, 2);
        Grid.SetColumn(badgeText, 3);
        grid.Children.Add(indicator);
        grid.Children.Add(glyphText);
        grid.Children.Add(textStack);
        grid.Children.Add(badgeText);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Tag = tag,
        };

        return new NavButtonParts
        {
            Button = button,
            Indicator = indicator,
            LabelText = labelText,
            BadgeText = badgeText,
        };
    }

    /// <summary>切换选中态：指示条 + 主标签前景/字重 + 按钮背景（原实现仅半透明背景，区分度不足）。</summary>
    private void SetActive(NavButtonParts parts)
    {
        if (_activeParts is not null)
        {
            _activeParts.Button.Background = new SolidColorBrush(Colors.Transparent);
            _activeParts.Indicator.Background = new SolidColorBrush(Colors.Transparent);
            _activeParts.LabelText.Foreground = Res<SolidColorBrush>("TextBrush") ?? new SolidColorBrush(Colors.White);
            _activeParts.LabelText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        }

        parts.Button.Background = Res<SolidColorBrush>("Surface2Brush") ?? new SolidColorBrush(Colors.Gray);
        parts.Indicator.Background = Res<SolidColorBrush>("AccentBrush") ?? new SolidColorBrush(Colors.LightBlue);
        parts.LabelText.Foreground = Res<SolidColorBrush>("AccentBrush") ?? new SolidColorBrush(Colors.LightBlue);
        parts.LabelText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _activeParts = parts;
    }

    /// <summary>设置导航项状态角标（如 update=“新”、queue=积压数）；text 为空则隐藏。</summary>
    public void SetNavBadge(string tag, string? text)
    {
        if (_navButtons.TryGetValue(tag, out var parts))
        {
            parts.BadgeText.Text = text ?? "";
            parts.BadgeText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ---------- 页面切换 ----------

    /// <summary>
    /// 切换页面。用 ContentControl 承载而非 Frame —— WinUI 3 的 Frame 在 unpackaged 形态
    /// 下初始化即 fail-fast（0xC000027B）。视图实例缓存，切回不重建（保留滚动/输入状态）。
    /// </summary>
    private void NavigateTo(Type viewType)
    {
        App.Trace($"NavigateTo {viewType.Name}: begin");
        try
        {
            if (!_viewCache.TryGetValue(viewType, out var view))
            {
                view = Activator.CreateInstance(viewType) as UIElement;
                if (view is null)
                {
                    App.Trace($"NavigateTo {viewType.Name}: instance null");
                    return;
                }
                _viewCache[viewType] = view;
            }
            ContentHost.Content = view;
            App.Trace($"NavigateTo {viewType.Name}: ok");
        }
        catch (Exception ex)
        {
            App.Trace($"NavigateTo {viewType.Name}: FAILED {ex.GetType().Name}: {ex.Message}");
            // 单个视图构造失败不应拖垮整个窗口：保持当前页面，但记录异常便于诊断
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "view_error.log"),
                    viewType.FullName + "\n" + ex);
            }
            catch { }
        }
    }

    private void NavigateToTag(string tag)
    {
        if (Views.TryGetValue(tag, out var viewType))
        {
            NavigateTo(viewType);
            if (_navButtons.TryGetValue(tag, out var parts))
            {
                SetActive(parts);
            }
        }
    }

    // ---------- ⌘K 命令面板 ----------

    private void BuildPaletteItems()
    {
        foreach (var (group, items) in NavGroups)
        {
            foreach (var (tag, label, _, tip) in items)
            {
                if (Views.ContainsKey(tag))
                {
                    _paletteItems.Add(new PaletteItem(tag, label, tip, group));
                }
            }
        }
    }

    private void TogglePalette()
    {
        PaletteOverlay.Visibility =
            PaletteOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (PaletteOverlay.Visibility == Visibility.Visible)
        {
            PaletteInput.Text = "";
            RefreshPaletteList(_paletteItems);
            PaletteInput.Focus(FocusState.Programmatic);
        }
    }

    private void RefreshPaletteList(List<PaletteItem> items)
    {
        PaletteList.ItemsSource = items;
        PaletteList.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private void PaletteInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = (PaletteInput.Text ?? "").Trim();
        if (string.IsNullOrEmpty(query))
        {
            RefreshPaletteList(_paletteItems);
            return;
        }
        var matched = _paletteItems.FindAll(p =>
            p.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Tip.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Tag.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Group.Contains(query, StringComparison.OrdinalIgnoreCase));
        RefreshPaletteList(matched);
    }

    private void PaletteInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down:
                if (PaletteList.SelectedIndex < PaletteList.Items.Count - 1)
                {
                    PaletteList.SelectedIndex++;
                }
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Up:
                if (PaletteList.SelectedIndex > 0)
                {
                    PaletteList.SelectedIndex--;
                }
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter:
                if (PaletteList.SelectedItem is PaletteItem item)
                {
                    NavigateToTag(item.Tag);
                    PaletteOverlay.Visibility = Visibility.Collapsed;
                }
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                PaletteOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                break;
        }
    }

    private void PaletteList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteItem item)
        {
            NavigateToTag(item.Tag);
            PaletteOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 基础设施 ----------

    /// <summary>从应用资源字典取样式，缺失时返回 null（不抛异常）。</summary>
    private Style? TryFindResource(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Style style ? style : null;

    /// <summary>取主题画笔（ThemeDictionaries 合并视图）。</summary>
    private static T? Res<T>(string key) where T : class
        => Application.Current.Resources.TryGetValue(key, out var value) && value is T typed ? typed : null;

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
}
