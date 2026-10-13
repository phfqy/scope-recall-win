using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ScopeRecallWin.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;
using Windows.System;

namespace ScopeRecallWin.Views;

public sealed partial class SearchView : UserControl
{
    private readonly IStorageService? _storage;
    private string? _dbPath;
    private List<EventRow> _rows = new();
    private bool _loaded;

    public SearchView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Loaded += SearchView_Loaded;
    }

    private void SearchView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        var path = root is null ? null : PathDiscovery.MemoryDbPath(root);
        _dbPath = path is not null && File.Exists(path) ? path : null;
    }

    private async void BtnSearch_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private void TxtKeyword_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _ = SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        var keyword = (TxtKeyword.Text ?? "").Trim();
        if (keyword.Length == 0)
        {
            TxtResult.Text = "请输入关键词。";
            ResultList.ItemsSource = null;
            return;
        }
        if (_dbPath is null || _storage is null)
        {
            TxtResult.Text = "未找到记忆库：请先在「安装」页选择部署目录。";
            return;
        }

        var rows = (await _storage.LexicalSearchAsync(_dbPath, keyword, 200)).ToList();
        _rows = rows;

        var vms = rows.Select(e => new
        {
            Head = $"{FormatTs(e.OccurredAt)} · {e.Role} · {e.SourceId ?? e.EventId}",
            Summary = Truncate(e.Content, 180),
        }).ToList();

        TxtResult.Text = $"「{keyword}」命中 {rows.Count} 条（按时间倒序，最多 200）";
        ResultList.ItemsSource = vms;
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

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var index = ResultList.Items.IndexOf(e.ClickedItem);
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
        Field("session", row.SessionId ?? "—");
        Field("发生时间", FormatTs(row.OccurredAt));
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
