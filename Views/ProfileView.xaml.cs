using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ScopeRecallWin.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;
using Windows.UI;

namespace ScopeRecallWin.Views;

public sealed partial class ProfileView : UserControl
{
    /// <summary>关系图形态。</summary>
    private const string GraphRadial = "radial";
    private const string GraphTree = "tree";

    /// <summary>关系图最多展示的主题数（超出时提示）。</summary>
    private const int GraphTopicLimit = 18;

    private readonly IStorageService? _storage;
    private string? _dbPath;
    private List<ProfileGroupRow> _groups = new();
    private double _zoom = 1.0;
    private bool _loaded;

    public ProfileView()
    {
        InitializeComponent();

        _storage = ServiceLocator.Get<IStorageService>();

        PickerView.SetItems(new[] { "关系图", "列表" }, 0, new[] { "graph", "list" });
        PickerGraphKind.SetItems(new[] { "中心辐射图", "树状图" }, 0, new[] { GraphRadial, GraphTree });
        PickerSort.SetItems(
            new[] { "按条目数排序", "按最近更新排序", "按名称排序", "按待确认数排序" },
            0,
            new[] { "count", "recent", "name", "proposed" });

        PickerView.SelectionChanged += (_, _) => ApplyViewMode();
        PickerGraphKind.SelectionChanged += (_, _) => RenderGraph();
        PickerSort.SelectionChanged += (_, _) => RenderContent();

        Loaded += (_, _) => UiThread.Run(DispatcherQueue, "ProfileView.Loaded", LoadAsync);
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "ProfileView.Refresh", LoadAsync);

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom + 0.15);

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom - 0.15);

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, 0.6, 2.0);
        if (GraphCanvas.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = _zoom;
            scale.ScaleY = _zoom;
        }
    }

    // ---------- 数据 ----------

    private string? ResolveDbPath()
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null)
        {
            return null;
        }
        var path = PathDiscovery.MemoryDbPath(root);
        return File.Exists(path) ? path : null;
    }

    private async Task LoadAsync()
    {
        _dbPath = ResolveDbPath();
        if (_dbPath is null || _storage is null)
        {
            TxtTotal.Text = "未找到记忆库：请先到「安装」页选择部署目录。";
            GraphCanvas.Children.Clear();
            GroupList.ItemsSource = null;
            return;
        }

        _groups = (await _storage.GetProfileGroupsAsync(_dbPath, 300)).ToList();
        RenderContent();
    }

    private void ApplyViewMode()
    {
        var isGraph = (PickerView.SelectedTag ?? "graph") == "graph";
        GraphHost.Visibility = isGraph ? Visibility.Visible : Visibility.Collapsed;
        GroupList.Visibility = isGraph ? Visibility.Collapsed : Visibility.Visible;
        PickerGraphKind.Visibility = isGraph ? Visibility.Visible : Visibility.Collapsed;
        PickerSort.Visibility = isGraph ? Visibility.Collapsed : Visibility.Visible;
        BtnZoomIn.Visibility = isGraph ? Visibility.Visible : Visibility.Collapsed;
        BtnZoomOut.Visibility = isGraph ? Visibility.Visible : Visibility.Collapsed;
        RenderContent();
    }

    /// <summary>按当前排序方式整理数据。</summary>
    private List<ProfileGroupRow> SortedGroups() => (PickerSort.SelectedTag ?? "count") switch
    {
        "recent" => _groups
            .OrderByDescending(g => TimestampNormalizer.Parse(g.NewestFrom ?? ""))
            .ToList(),
        "name" => _groups.OrderBy(g => g.Subject, StringComparer.CurrentCulture).ToList(),
        "proposed" => _groups.OrderByDescending(g => g.ClaimCount - g.ActiveCount).ToList(),
        _ => _groups.OrderByDescending(g => g.ClaimCount).ToList(),
    };

    private void RenderContent()
    {
        if ((PickerView.SelectedTag ?? "graph") == "graph")
        {
            RenderGraph();
        }
        else
        {
            RenderList();
        }
    }

    private void RenderList()
    {
        var sorted = SortedGroups();
        GroupList.ItemsSource = sorted.Select(g => new
        {
            g.Subject,
            CountText = $"{g.ClaimCount} 条 · 已生效 {g.ActiveCount}",
            NewestText = g.NewestFrom is null
                ? ""
                : $"更新 {TimestampNormalizer.Parse(g.NewestFrom):yyyy-MM-dd}",
        }).ToList();
        TxtTotal.Text = $"共 {_groups.Count} 个主题（{SortName()}）。点击任一行可查看该主题下的记忆条目。";
    }

    private string SortName() => PickerSort.SelectedLabel;

    // ---------- 关系图 ----------

    private void RenderGraph()
    {
        GraphCanvas.Children.Clear();
        GraphCanvas.RenderTransform = new ScaleTransform { ScaleX = _zoom, ScaleY = _zoom };

        if (_groups.Count == 0)
        {
            TxtTotal.Text = "还没有可展示的主题；等记忆积累出主题后这里会自动出现关系图。";
            return;
        }

        // 关系图按条目数排序，取前 N 个主题，避免节点过多无法阅读
        var shown = _groups.OrderByDescending(g => g.ClaimCount).Take(GraphTopicLimit).ToList();
        var hidden = _groups.Count - shown.Count;

        if ((PickerGraphKind.SelectedTag ?? GraphRadial) == GraphTree)
        {
            DrawTree(shown);
        }
        else
        {
            DrawRadial(shown);
        }

        TxtTotal.Text = $"关系图：1 个记忆库 · {shown.Count} 个主题" +
                        (hidden > 0 ? $"（另有 {hidden} 个主题条目较少，未在图中显示）" : "") +
                        "。点击主题节点查看其记忆条目；可用「放大 / 缩小」调整。";
    }

    /// <summary>中心辐射图：记忆库在中心，主题按条目数决定大小环绕分布。</summary>
    private void DrawRadial(List<ProfileGroupRow> groups)
    {
        const double width = 1100;
        const double height = 640;
        var cx = width / 2;
        var cy = height / 2;
        var maxCount = Math.Max(1, groups.Max(g => g.ClaimCount));

        // 中心节点
        AddNode(cx, cy, 76, "记忆库", null, isCenter: true);

        var radius = Math.Min(width, height) * 0.36;
        var accent = ThemeService.IsDark
            ? Color.FromArgb(0xFF, 0x4F, 0xC3, 0xF7)
            : Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD);

        for (int i = 0; i < groups.Count; i++)
        {
            var angle = 2 * Math.PI * i / groups.Count - Math.PI / 2;
            var x = cx + radius * Math.Cos(angle);
            var y = cy + radius * Math.Sin(angle);

            // 连线
            GraphCanvas.Children.Add(new Line
            {
                X1 = cx,
                Y1 = cy,
                X2 = x,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)),
                StrokeThickness = 1.5,
            });

            var size = 22 + 40 * Math.Sqrt((double)groups[i].ClaimCount / maxCount);
            AddNode(x, y, size, Shorten(groups[i].Subject, 7), groups[i], isCenter: false);
        }
    }

    /// <summary>树状图：根 → 主题两层结构，突出层级从属关系。</summary>
    private void DrawTree(List<ProfileGroupRow> groups)
    {
        const double leftX = 150;
        const double topicX = 470;
        const double rowHeight = 34;

        var rootY = Math.Max(120, groups.Count * rowHeight / 2.0);
        AddNode(leftX, rootY, 76, "记忆库", null, isCenter: true);

        var accent = ThemeService.IsDark
            ? Color.FromArgb(0xFF, 0x4F, 0xC3, 0xF7)
            : Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD);
        var maxCount = Math.Max(1, groups.Max(g => g.ClaimCount));

        for (int i = 0; i < groups.Count; i++)
        {
            var y = 40 + i * rowHeight;
            var group = groups[i];

            // 折线：根 → 主题
            GraphCanvas.Children.Add(new Line
            {
                X1 = leftX + 76,
                Y1 = rootY,
                X2 = topicX - 12,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(0x44, accent.R, accent.G, accent.B)),
                StrokeThickness = 1.5,
            });

            var width = 160 * (0.4 + 0.6 * Math.Sqrt((double)group.ClaimCount / maxCount));
            var bar = new Border
            {
                Width = width,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                Tag = group.Subject,
            };
            var text = new TextBlock
            {
                Text = $"{Shorten(group.Subject, 9)}  {group.ClaimCount}",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
            };
            bar.Child = text;
            bar.Tapped += Node_Tapped;
            Canvas.SetLeft(bar, topicX - width + 60);
            Canvas.SetTop(bar, y - 12);
            GraphCanvas.Children.Add(bar);
        }
    }

    /// <summary>添加圆形节点（中心节点更大，主题节点按条目数缩放）。</summary>
    private void AddNode(double x, double y, double size, string label, ProfileGroupRow? group, bool isCenter)
    {
        var accent = ThemeService.IsDark
            ? Color.FromArgb(0xFF, 0x4F, 0xC3, 0xF7)
            : Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD);

        var circle = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(isCenter
                ? Color.FromArgb(0xCC, accent.R, accent.G, accent.B)
                : Color.FromArgb(0x44, accent.R, accent.G, accent.B)),
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1.5,
            Tag = group?.Subject,
        };
        if (!isCenter)
        {
            circle.Tapped += Node_Tapped;
        }
        Canvas.SetLeft(circle, x - size / 2);
        Canvas.SetTop(circle, y - size / 2);
        GraphCanvas.Children.Add(circle);

        var caption = new TextBlock
        {
            Text = isCenter ? label : $"{label}\n{group?.ClaimCount ?? 0} 条",
            FontSize = isCenter ? 13.5 : 11.5,
            FontWeight = isCenter ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            IsHitTestVisible = false,
            Tag = group?.Subject,
        };
        Canvas.SetLeft(caption, x - 60);
        Canvas.SetTop(caption, y + size / 2 + 4);
        GraphCanvas.Children.Add(caption);
    }

    private static string Shorten(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max] + "…";

    private void Node_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string subject || subject.Length == 0)
        {
            return;
        }
        UiThread.Run(DispatcherQueue, "ProfileView.NodeTap", () => OpenSubjectAsync(subject));
    }

    private async Task OpenSubjectAsync(string subject)
    {
        var panel = await BuildSubjectPanelAsync(subject);
        Drawer.Open($"主题 {subject}", panel);
    }

    // ---------- 列表点击 / 抽屉 ----------

    private void GroupList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var index = GroupList.Items.IndexOf(e.ClickedItem);
        var sorted = SortedGroups();
        if (index < 0 || index >= sorted.Count)
        {
            return;
        }
        var subject = sorted[index].Subject;
        UiThread.Run(DispatcherQueue, "ProfileView.ListClick", () => OpenSubjectAsync(subject));
    }

    private async Task<StackPanel> BuildSubjectPanelAsync(string subject)
    {
        var panel = new StackPanel { Spacing = 8 };

        var group = _groups.FirstOrDefault(g => g.Subject == subject);
        panel.Children.Add(new TextBlock
        {
            Text = group is null
                ? subject
                : $"{subject} · {group.ClaimCount} 条（已生效 {group.ActiveCount}）",
            Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
        });

        if (_storage is null || _dbPath is null)
        {
            return panel;
        }

        var claims = (await _storage.EntitySearchAsync(_dbPath, subject, 100)).ToList();
        if (claims.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "没有可直接展示的条目。",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            });
        }

        foreach (var c in claims)
        {
            var box = new Border
            {
                Background = (Brush)Application.Current.Resources["Surface2Brush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
            };
            var inner = new StackPanel { Spacing = 3 };
            inner.Children.Add(new TextBlock
            {
                Text = c.Predicate,
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            inner.Children.Add(new TextBlock
            {
                Text = $"状态：{StatusVisuals.StateLabel(c.State)} · 版本 {c.CurrentRevision} · 类型 {c.Kind}",
                FontSize = 11,
                Foreground = StatusVisuals.BrushOf(c.State),
            });
            inner.Children.Add(new TextBlock
            {
                Text = c.ClaimId,
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                IsTextSelectionEnabled = true,
            });
            box.Child = inner;
            panel.Children.Add(box);
        }

        return panel;
    }
}
