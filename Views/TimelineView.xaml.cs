using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScopeRecallWin.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class TimelineView : UserControl
{
    private readonly IStorageService? _storage;
    private string? _dbPath;
    private List<EventRow> _rows = new();
    private bool _loaded;

    public TimelineView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Loaded += TimelineView_Loaded;
        // 角色 = 这条内容由谁产生：用户输入 / 助手回复 / 工具调用结果
        PickerRole.SetItems(
            new[] { "全部角色", "用户", "助手", "工具" },
            selectedIndex: 0,
            tags: new[] { "all", "user", "assistant", "tool" });
        PickerRange.SetItems(
            new[] { "最近 500 条", "最近 2000 条", "全部" },
            selectedIndex: 0,
            tags: new[] { "500", "2000", "all" });
    }

    private void TimelineView_Loaded(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "TimelineView.TimelineView_Loaded", async () =>
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            await ReloadAsync();

        });
    }

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

    private async Task ReloadAsync()
    {
        _dbPath = ResolveDbPath();
        if (_dbPath is null || _storage is null)
        {
            TxtTotal.Text = "未找到记忆库：请先在「安装」页选择部署目录。";
            EventList.ItemsSource = null;
            return;
        }

        var role = PickerRole.SelectedTag ?? "all";
        var limit = PickerRange.SelectedTag switch
        {
            "2000" => 2000,
            "all" => 100000,
            _ => 500,
        };
        var query = (TxtSearch.Text ?? "").Trim();
        if (query.Length == 0)
        {
            query = null;
        }

        var (rows, total) = await _storage.GetEventsAsync(_dbPath, role, null, null, query, limit);
        _rows = rows.ToList();

        var vms = _rows.Select(e => new
        {
            TimeText = $"{FormatTs(e.OccurredAt)} · {e.Role} · {e.SourceId ?? e.EventId}",
            Summary = Truncate(e.Content, 160),
        }).ToList();

        TxtTotal.Text = $"命中 {total} 条，展示 {_rows.Count} 条（按时间倒序）";
        EventList.ItemsSource = vms;
    }

    private static string FormatTs(string? raw)
    {
        var ts = Helpers.TimestampNormalizer.Parse(raw ?? "");
        return ts == default ? raw ?? "—" : ts.ToLocalTime().ToString("MM-dd HH:mm");
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "TimelineView.BtnRefresh_Click", ReloadAsync);

    private void BtnApply_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "TimelineView.BtnApply_Click", ReloadAsync);

    private void EventList_ItemClick(object sender, ItemClickEventArgs e)
    {
        // 通过摘要反查行（时间线投影为匿名对象，用索引匹配）
        var index = EventList.Items.IndexOf(e.ClickedItem);
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }
        var row = _rows[index];

        var panel = new StackPanel { Spacing = 10 };
        void Field(string label, string value)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var k = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            };
            var v = new TextBlock
            {
                Text = value,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            Grid.SetColumn(k, 0);
            Grid.SetColumn(v, 1);
            grid.Children.Add(k);
            grid.Children.Add(v);
            panel.Children.Add(grid);
        }

        Field("event_id", row.EventId);
        Field("角色", row.Role);
        Field("来源", row.Origin ?? "—");
        Field("session", row.SessionId ?? "—");
        Field("source_id", row.SourceId ?? "—");
        Field("发生时间", FormatTs(row.OccurredAt));
        Field("记录时间", FormatTs(row.RecordedAt));
        Field("capture", row.CaptureState ?? "—");

        panel.Children.Add(new TextBlock
        {
            Text = "内容",
            Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
        });
        panel.Children.Add(new Border
        {
            Background = (Brush)Application.Current.Resources["Surface2Brush"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = new TextBlock
            {
                Text = row.Content,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        });

        Drawer.Open($"事件 {row.EventId}", panel);
    }
}
