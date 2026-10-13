using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScopeRecallWin.Controls;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class ClaimsView : UserControl
{
    private const int PageSize = 100;

    private readonly IStorageService? _storage;
    private string? _dbPath;
    private List<ClaimRow> _rows = new();
    private int _page = 1;
    private long _total;
    private bool _loaded;

    public ClaimsView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Table.RowTapped += Table_RowTapped;
        Loaded += ClaimsView_Loaded;
    }

    private async void ClaimsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await ReloadAsync();
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
            Table.BindRows(Array.Empty<ClaimRow>(), Columns());
            return;
        }

        var status = ((ComboBoxItem)CmbStatus.SelectedItem)?.Content?.ToString();
        if (status == "全部状态")
        {
            status = "all";
        }
        var query = (TxtSearch.Text ?? "").Trim();
        if (query.Length == 0)
        {
            query = null;
        }

        var (rows, total) = await _storage.GetClaimsAsync(_dbPath, status, query, _page, PageSize);
        _rows = rows.ToList();
        _total = total;

        TxtTotal.Text = $"共 {_total} 条 · 第 {_page} 页（每页 {PageSize}）";
        Table.BindRows(_rows, Columns());
        BtnPrev.IsEnabled = _page > 1;
        BtnNext.IsEnabled = _page * PageSize < _total;
    }

    private static IReadOnlyList<TableColumn> Columns() => new[]
    {
        new TableColumn { Header = "claim_id", Width = 190, Value = r => ((ClaimRow)r).ClaimId },
        new TableColumn { Header = "subject", Width = 160, Value = r => ((ClaimRow)r).Subject },
        new TableColumn { Header = "predicate", Width = 160, Value = r => ((ClaimRow)r).Predicate },
        new TableColumn { Header = "kind", Width = 90, Value = r => ((ClaimRow)r).Kind },
        new TableColumn { Header = "状态", Width = 90, Value = r => ((ClaimRow)r).State },
        new TableColumn { Header = "rev", Width = 50, Value = r => ((ClaimRow)r).CurrentRevision.ToString() },
        new TableColumn { Header = "版本", Width = 55, Value = r => ((ClaimRow)r).VersionCount.ToString() },
        new TableColumn { Header = "证据", Width = 55, Value = r => ((ClaimRow)r).EvidenceCount.ToString() },
        new TableColumn { Header = "抑制", Width = 55, Value = r => ((ClaimRow)r).Suppressed ? "是" : "" },
    };

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void BtnFilter_Click(object sender, RoutedEventArgs e)
    {
        _page = 1;
        await ReloadAsync();
    }

    private async void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        _page = 1;
        await ReloadAsync();
    }

    private async void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1)
        {
            _page--;
            await ReloadAsync();
        }
    }

    private async void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_page * PageSize < _total)
        {
            _page++;
            await ReloadAsync();
        }
    }

    // ---------- 详情抽屉 ----------

    private async void Table_RowTapped(object? sender, object row)
    {
        if (row is not ClaimRow claim || _dbPath is null || _storage is null)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 12 };

        void Section(string title)
        {
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
            });
        }

        void Field(string label, string value)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
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

        Field("claim_id", claim.ClaimId);
        Field("subject", claim.Subject);
        Field("predicate", claim.Predicate);
        Field("kind", claim.Kind);
        Field("状态", claim.State + (claim.Suppressed ? "（suppressed）" : ""));
        Field("当前版本", claim.CurrentRevision.ToString());

        Section("历史版本");
        var versions = await _storage.GetClaimVersionsAsync(_dbPath, claim.ClaimId);
        foreach (var v in versions)
        {
            var box = new Border
            {
                Background = (Brush)Application.Current.Resources["Surface2Brush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
            };
            var inner = new StackPanel { Spacing = 4 };
            inner.Children.Add(new TextBlock
            {
                Text = $"rev {v.Revision} · {v.State}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            inner.Children.Add(new TextBlock
            {
                Text = $"valid {v.ValidFrom ?? "—"} ~ {v.ValidTo ?? "—"}{(v.ReplacesRevision is null ? "" : $" · 替换 rev{v.ReplacesRevision}")}",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            });
            inner.Children.Add(new TextBlock
            {
                Text = v.PayloadJson,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                MaxHeight = 120,
            });
            box.Child = inner;
            panel.Children.Add(box);
        }

        Section($"证据引文（{claim.EvidenceCount}）");
        var evidence = await _storage.GetClaimEvidenceAsync(_dbPath, claim.ClaimId);
        foreach (var e in evidence)
        {
            var box = new Border
            {
                Background = (Brush)Application.Current.Resources["Surface2Brush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
            };
            var inner = new StackPanel { Spacing = 2 };
            inner.Children.Add(new TextBlock
            {
                Text = $"{e.Relation} ← {e.SourceRef}@{e.SourceRevision}（{e.EventRole ?? "?"}）",
                FontSize = 11.5,
            });
            inner.Children.Add(new TextBlock
            {
                Text = e.Quote ?? e.EventExcerpt ?? "（无引文）",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            });
            box.Child = inner;
            panel.Children.Add(box);
        }

        Drawer.Open($"claim {claim.ClaimId}", panel);
    }
}
