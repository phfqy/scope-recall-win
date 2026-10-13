using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

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

    /// <summary>导航分组：组名 + （Tag / 标题 / Segoe 图标码 / 悬浮提示）。</summary>
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

    private Button? _activeNavButton;

    public MainWindow()
    {
        InitializeComponent();

        // Mica 背景（Fluent）：Windows 11 生效；旧系统回退纯色（try/catch 兜底）
        TrySetMica();

        BuildNavigation();
        NavigateTo(typeof(Views.OverviewView));

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

    /// <summary>按 NavGroups 生成左导航项（分组标题 + 图标按钮）。</summary>
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

                var btn = CreateNavButton(label, glyph, tip);
                btn.Click += (_, _) =>
                {
                    NavigateTo(viewType);
                    SetActive(btn);
                };
                NavPanel.Children.Add(btn);

                if (tag == "overview")
                {
                    SetActive(btn);
                }
            }
        }
    }

    private Button CreateNavButton(string label, string glyph, string tip)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(6, 2, 6, 2),
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var btn = new Button
        {
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
        };
        ToolTipService.SetToolTip(btn, tip);
        return btn;
    }

    private void SetActive(Button btn)
    {
        if (_activeNavButton is not null)
        {
            _activeNavButton.Background = new SolidColorBrush(Colors.Transparent);
        }
        _activeNavButton = btn;
        btn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>
    /// 切换页面。用 ContentControl 承载而非 Frame —— WinUI 3 的 Frame 在 unpackaged 形态
    /// 下初始化即 fail-fast（0xC000027B）。ContentControl 无此限制，配合
    /// ContentTransitions 仍保留入场动画。
    /// </summary>
    private void NavigateTo(Type viewType)
    {
        try
        {
            if (Activator.CreateInstance(viewType) is UIElement view)
            {
                ContentHost.Content = view;
            }
        }
        catch (Exception ex)
        {
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

    /// <summary>从应用资源字典取样式，缺失时返回 null（不抛异常）。</summary>
    private Style? TryFindResource(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Style style ? style : null;

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
