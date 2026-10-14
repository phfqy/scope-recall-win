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

public sealed partial class QueryView : UserControl
{
    private readonly IStorageService? _storage;
    private string? _dbPath;
    private List<EventRow> _lexicalRows = new();
    private bool _loaded;

    public QueryView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Loaded += QueryView_Loaded;
        QueryTabs.SelectionChanged += QueryTabs_SelectionChanged;
    }

    private void QueryView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        var path = root is null ? null : PathDiscovery.MemoryDbPath(root);
        _dbPath = path is not null && File.Exists(path) ? path : null;
        ShowPanel(0);
    }

    private void QueryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowPanel(QueryTabs.SelectedIndex);
    }

    private void ShowPanel(int index)
    {
        PanelLexical.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelTrace.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelProfile.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelEntity.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 2)
        {
            _ = LoadProfileAsync();
        }
    }

    private string? DbPathOrWarn(TextBlock target)
    {
        if (_dbPath is null || _storage is null)
        {
            target.Text = "未找到记忆库：请先在「安装」页选择部署目录。";
            return null;
        }
        return _dbPath;
    }

    // ---------- 词法检索 ----------

    private void BtnLexical_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "QueryView.BtnLexical_Click", LexicalAsync);

    private async Task LexicalAsync()
    {
        var keyword = (TxtLexical.Text ?? "").Trim();
        if (keyword.Length == 0)
        {
            TxtLexicalResult.Text = "请输入关键词。";
            return;
        }
        var db = DbPathOrWarn(TxtLexicalResult);
        if (db is null)
        {
            return;
        }
        _lexicalRows = (await _storage!.LexicalSearchAsync(db, keyword, 200)).ToList();
        LexicalList.ItemsSource = _lexicalRows.Select(e => new
        {
            Head = $"{FormatTs(e.OccurredAt)} · {e.Role} · {e.SourceId ?? e.EventId}",
            Summary = Truncate(e.Content, 180),
        }).ToList();
        TxtLexicalResult.Text = $"「{keyword}」命中 {_lexicalRows.Count} 条";
    }

    // ---------- 链路追溯 ----------

    private void BtnTrace_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "QueryView.BtnTrace_Click", TraceAsync);

    private async Task TraceAsync()
    {
        var refId = (TxtTrace.Text ?? "").Trim();
        if (refId.Length == 0)
        {
            TxtTraceResult.Text = "请输入 claim_id 或 event_id。";
            return;
        }
        var db = DbPathOrWarn(TxtTraceResult);
        if (db is null)
        {
            return;
        }
        var steps = (await _storage!.TraceAsync(db, refId)).ToList();
        TraceList.ItemsSource = steps.Select(s =>
            $"[{s.Kind}] {s.Relation} → {s.Ref}{(s.Detail is null ? "" : $" · {s.Detail}")}").ToList();
        TxtTraceResult.Text = steps.Count == 0
            ? $"「{refId}」没有关联链路（可能 ID 不存在，或它是源事件无上游）。"
            : $"「{refId}」关联 {steps.Count} 条链路";
    }

    // ---------- 主题聚合 ----------

    private async Task LoadProfileAsync()
    {
        var db = DbPathOrWarn(TxtProfileResult);
        if (db is null)
        {
            return;
        }
        var groups = (await _storage!.GetProfileGroupsAsync(db, 100)).ToList();
        ProfileList.ItemsSource = groups.Select(g =>
            $"{g.Subject} — {g.ClaimCount} 条（active {g.ActiveCount}）").ToList();
        TxtProfileResult.Text = $"共 {groups.Count} 个主题";
    }

    // ---------- 实体匹配 ----------

    private void BtnEntity_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "QueryView.BtnEntity_Click", EntityAsync);

    private async Task EntityAsync()
    {
        var entity = (TxtEntity.Text ?? "").Trim();
        if (entity.Length == 0)
        {
            TxtEntityResult.Text = "请输入实体。";
            return;
        }
        var db = DbPathOrWarn(TxtEntityResult);
        if (db is null)
        {
            return;
        }
        var claims = (await _storage!.EntitySearchAsync(db, entity, 100)).ToList();
        EntityTable.BindRows(claims, new[]
        {
            new TableColumn { Header = "记忆编号", Width = 200, Value = r => ((ClaimRow)r).ClaimId },
            new TableColumn { Header = "属性", Width = 170, Value = r => ((ClaimRow)r).Predicate },
            new TableColumn { Header = "类型", Width = 80, Value = r => ((ClaimRow)r).Kind },
            new TableColumn
            {
                Header = "状态", Width = 90,
                Value = r => StatusVisuals.StateLabel(((ClaimRow)r).State),
                Color = r => StatusVisuals.BrushOf(((ClaimRow)r).State),
                Tooltip = r => StatusVisuals.OriginalTerm(((ClaimRow)r).State),
            },
            new TableColumn { Header = "版本", Width = 50, Value = r => ((ClaimRow)r).CurrentRevision.ToString() },
        });
        TxtEntityResult.Text = $"「{entity}」匹配 {claims.Count} 条";
    }

    // ---------- 共享 ----------

    private void AnyEnter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }
        if (ReferenceEquals(sender, TxtLexical))
        {
            _ = LexicalAsync();
        }
        else if (ReferenceEquals(sender, TxtTrace))
        {
            _ = TraceAsync();
        }
        else if (ReferenceEquals(sender, TxtEntity))
        {
            _ = EntityAsync();
        }
    }

    private void SharedList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var index = LexicalList.Items.IndexOf(e.ClickedItem);
        if (index < 0 || index >= _lexicalRows.Count)
        {
            return;
        }
        var row = _lexicalRows[index];
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "内容", Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style });
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
}
