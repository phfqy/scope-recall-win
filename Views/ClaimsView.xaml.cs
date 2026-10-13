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
        PickerStatus.SetItems(
            new[] { "全部状态", "已生效", "待确认", "有争议", "已被取代", "已撤回" },
            selectedIndex: 0,
            tags: new[] { "all", "active", "proposed", "disputed", "superseded", "retracted" });
        Loaded += ClaimsView_Loaded;
    }

    private void ClaimsView_Loaded(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "ClaimsView.ClaimsView_Loaded", async () =>
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
            Table.BindRows(Array.Empty<ClaimRow>(), Columns());
            return;
        }

        var status = PickerStatus.SelectedTag ?? "all";
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
        new TableColumn { Header = "记忆编号", Width = 190, Value = r => ((ClaimRow)r).ClaimId },
        new TableColumn { Header = "主题", Width = 150, Value = r => ((ClaimRow)r).Subject },
        new TableColumn { Header = "属性", Width = 150, Value = r => ((ClaimRow)r).Predicate },
        new TableColumn { Header = "类型", Width = 80, Value = r => ((ClaimRow)r).Kind },
        new TableColumn
        {
            Header = "状态", Width = 90,
            Value = r => StatusVisuals.StateLabel(((ClaimRow)r).State),
            Color = r => StatusVisuals.BrushOf(((ClaimRow)r).State),
            Tooltip = r => StatusVisuals.OriginalTerm(((ClaimRow)r).State),
        },
        new TableColumn { Header = "版本", Width = 50, Value = r => ((ClaimRow)r).CurrentRevision.ToString() },
        new TableColumn { Header = "历史版本数", Width = 85, Value = r => ((ClaimRow)r).VersionCount.ToString() },
        new TableColumn { Header = "证据数", Width = 65, Value = r => ((ClaimRow)r).EvidenceCount.ToString() },
        new TableColumn { Header = "已停用", Width = 60, Value = r => ((ClaimRow)r).Suppressed ? "是" : "" },
    };

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "ClaimsView.BtnRefresh_Click", ReloadAsync);

    private void BtnFilter_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "ClaimsView.BtnFilter_Click", async () =>
        {
            _page = 1;
            await ReloadAsync();

        });
    }

    private void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "ClaimsView.BtnSearch_Click", async () =>
        {
            _page = 1;
            await ReloadAsync();

        });
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "ClaimsView.BtnPrev_Click", async () =>
        {
            if (_page > 1)
            {
                _page--;
                await ReloadAsync();
            }

        });
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "ClaimsView.BtnNext_Click", async () =>
        {
            if (_page * PageSize < _total)
            {
                _page++;
                await ReloadAsync();
            }

        });
    }

    // ---------- 详情抽屉 ----------

    private void Table_RowTapped(object? sender, object row)
    {
        UiThread.Run(DispatcherQueue, "ClaimsView.Table_RowTapped", async () =>
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

            Field("记忆编号", claim.ClaimId);
            Field("主题", claim.Subject);
            Field("属性", claim.Predicate);
            Field("类型", claim.Kind);
            Field("状态", StatusVisuals.StateLabel(claim.State) + (claim.Suppressed ? "（已停用）" : ""));
            Field("当前版本号", claim.CurrentRevision.ToString());

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
                    Text = $"版本 {v.Revision} · {StatusVisuals.StateLabel(v.State)}",
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

            Drawer.Open($"记忆条目 {claim.ClaimId}", panel);

        });
    }
}
