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

public sealed partial class AiChangesView : UserControl
{
    private readonly IStorageService? _storage;
    private string? _dbPath;
    private List<CandidateRow> _rows = new();
    private bool _loaded;

    public AiChangesView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Table.RowTapped += Table_RowTapped;
        Loaded += AiChangesView_Loaded;
    }

    private void AiChangesView_Loaded(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "AiChangesView.AiChangesView_Loaded", async () =>
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
            Table.BindRows(Array.Empty<CandidateRow>(), Columns());
            return;
        }

        var state = ((ComboBoxItem)CmbState.SelectedItem)?.Content?.ToString();
        if (state == "全部状态")
        {
            state = "all";
        }

        var (rows, total) = await _storage.GetCandidatesAsync(_dbPath, state, 300);
        _rows = rows.ToList();
        TxtTotal.Text = $"共 {total} 条候选，展示 {_rows.Count} 条（按更新时间倒序）";
        Table.BindRows(_rows, Columns());
    }

    private static IReadOnlyList<TableColumn> Columns() => new[]
    {
        new TableColumn { Header = "候选编号", Width = 230, Value = r => ((CandidateRow)r).CandidateRef },
        new TableColumn { Header = "版本", Width = 50, Value = r => ((CandidateRow)r).CandidateRevision.ToString() },
        new TableColumn
        {
            Header = "处理状态", Width = 130,
            Value = r => StatusVisuals.StateLabel(((CandidateRow)r).ProcessingState),
            Color = r => StatusVisuals.BrushOf(((CandidateRow)r).ProcessingState),
            Tooltip = r => StatusVisuals.OriginalTerm(((CandidateRow)r).ProcessingState),
        },
        new TableColumn { Header = "关联证据数", Width = 85, Value = r => ((CandidateRow)r).EvidenceCount.ToString() },
        new TableColumn { Header = "原因", Width = 170, Value = r => ((CandidateRow)r).Reason ?? "" },
        new TableColumn { Header = "更新时间", Width = 150, Value = r => ((CandidateRow)r).UpdatedAt ?? "" },
    };

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "AiChangesView.BtnRefresh_Click", ReloadAsync);

    private void BtnApply_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "AiChangesView.BtnApply_Click", ReloadAsync);

    private void Table_RowTapped(object? sender, object row)
    {
        UiThread.Run(DispatcherQueue, "AiChangesView.Table_RowTapped", async () =>
        {
            if (row is not CandidateRow candidate || _dbPath is null || _storage is null)
            {
                return;
            }
            var evidence = await _storage.GetCandidateEvidenceAsync(_dbPath, candidate.CandidateRef);

            var panel = new StackPanel { Spacing = 10 };
            void Field(string label, string value)
            {
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
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

            Field("candidate", candidate.CandidateRef);
            Field("revision", candidate.CandidateRevision.ToString());
            Field("状态", candidate.ProcessingState);
            Field("原因", candidate.Reason ?? "—");
            Field("指纹", candidate.EvidenceFingerprint ?? "—");
            Field("更新时间", candidate.UpdatedAt ?? "—");

            panel.Children.Add(new TextBlock
            {
                Text = $"关联证据（{evidence.Count}）",
                Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
            });
            foreach (var e in evidence)
            {
                panel.Children.Add(new Border
                {
                    Background = (Brush)Application.Current.Resources["Surface2Brush"],
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10),
                    Child = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"{e.SourceRef}@{e.SourceRevision} · {e.EventRole ?? "?"} · {e.ObservedAt ?? ""}",
                                FontSize = 11.5,
                            },
                            new TextBlock
                            {
                                Text = e.EventExcerpt ?? "",
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                            },
                        },
                    },
                });
            }

            Drawer.Open($"候选 {candidate.CandidateRef}", panel);

        });
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "AiChangesView.BtnExport_Click", async () =>
        {
            if (_rows.Count == 0)
            {
                return;
            }
            var keys = new[] { "candidate_ref", "revision", "processing_state", "reason", "evidence_count", "updated_at" };
            var rows = _rows.Select(r => new[]
            {
                r.CandidateRef, r.CandidateRevision.ToString(), r.ProcessingState, r.Reason, r.EvidenceCount.ToString(), r.UpdatedAt,
            }).ToList();
            await Exporter.SaveWithPickerAsync("ai-changes.json", Exporter.ToJson(keys, rows), ".json", "JSON");

        });
    }
}
