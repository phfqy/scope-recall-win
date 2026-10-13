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

public sealed partial class QueueView : UserControl
{
    private readonly IStorageService? _storage;
    private string? _dbPath;
    private List<WorkItemRow> _rows = new();
    private bool _loaded;

    public QueueView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Table.RowTapped += Table_RowTapped;
        PickerState.SetItems(
            new[] { "全部状态", "失败", "待处理", "已完成", "已废弃" },
            selectedIndex: 0,
            tags: new[] { "all", "failed", "pending", "done", "obsolete" });
        PickerType.SetItems(
            new[] { "全部类型", "向量化", "记忆整合", "候选评估", "重建索引" },
            selectedIndex: 0,
            tags: new[] { "all", "embed", "consolidate", "evaluate_candidate", "rebuild_projection" });
        Loaded += QueueView_Loaded;
    }

    private void QueueView_Loaded(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "QueueView.QueueView_Loaded", async () =>
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
            Table.BindRows(Array.Empty<WorkItemRow>(), Columns());
            return;
        }

        var state = PickerState.SelectedTag ?? "all";
        var type = PickerType.SelectedTag ?? "all";

        var (rows, total) = await _storage.GetWorkItemsAsync(_dbPath, state, type, 500);
        _rows = rows.ToList();
        TxtTotal.Text = $"共 {total} 条，展示 {_rows.Count} 条（失败/待处理置顶）";
        Table.BindRows(_rows, Columns());
    }

    private static IReadOnlyList<TableColumn> Columns() => new[]
    {
        new TableColumn { Header = "任务编号", Width = 210, Value = r => ((WorkItemRow)r).WorkId },
        new TableColumn { Header = "类型", Width = 130, Value = r => TypeLabel(((WorkItemRow)r).WorkType) },
        new TableColumn
        {
            Header = "状态", Width = 80,
            Value = r => StatusVisuals.StateLabel(((WorkItemRow)r).State),
            Color = r => StatusVisuals.BrushOf(((WorkItemRow)r).State),
            Tooltip = r => StatusVisuals.OriginalTerm(((WorkItemRow)r).State),
        },
        new TableColumn { Header = "尝试次数", Width = 75, Value = r => ((WorkItemRow)r).Attempt.ToString() },
        new TableColumn
        {
            Header = "失败原因", Width = 180,
            Value = r => StatusVisuals.ErrorLabel(((WorkItemRow)r).LastErrorCode),
            Color = r => ((WorkItemRow)r).LastErrorCode is null ? null : StatusVisuals.BrushOf("failed"),
            Tooltip = r => ((WorkItemRow)r).LastErrorCode is { } c ? $"原始错误码：{c}" : null,
        },
        new TableColumn { Header = "关联对象", Width = 170, Value = r => ((WorkItemRow)r).SubjectRef ?? "" },
        new TableColumn { Header = "可执行时间", Width = 150, Value = r => ((WorkItemRow)r).AvailableAt ?? "" },
    };

    /// <summary>任务类型中文名。</summary>
    private static string TypeLabel(string? raw) => raw switch
    {
        "embed" => "向量化",
        "consolidate" => "记忆整合",
        "evaluate_candidate" => "候选评估",
        "rebuild_projection" => "重建索引",
        _ => raw ?? "",
    };

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "QueueView.BtnRefresh_Click", ReloadAsync);

    private void BtnApply_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "QueueView.BtnApply_Click", ReloadAsync);

    private void Table_RowTapped(object? sender, object row)
    {
        UiThread.Run(DispatcherQueue, "QueueView.Table_RowTapped", async () =>
        {
            if (row is not WorkItemRow item || _dbPath is null || _storage is null)
            {
                return;
            }
            var errors = await _storage.GetWorkErrorsAsync(_dbPath, item.WorkId);
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

            Field("work_id", item.WorkId);
            Field("类型", TypeLabel(item.WorkType));
            Field("状态", StatusVisuals.StateLabel(item.State));
            Field("尝试次数", item.Attempt.ToString());
            Field("subject", item.SubjectRef ?? "—");
            Field("失败原因", StatusVisuals.ErrorLabel(item.LastErrorCode));
            Field("可用时间", item.AvailableAt ?? "—");

            panel.Children.Add(new TextBlock
            {
                Text = "错误详情",
                Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
            });
            if (errors.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "（无 error_details 记录）",
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                });
            }
            foreach (var e in errors)
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
                                Text = $"{e.Stage} · {e.ErrorCode ?? "?"}{(e.ErrorField is null ? "" : $" · {e.ErrorField}")}",
                                FontSize = 12,
                            },
                            new TextBlock
                            {
                                Text = e.RecordedAt ?? "",
                                FontSize = 11,
                                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                            },
                        },
                    },
                });
            }

            Drawer.Open($"任务 {item.WorkId}", panel);

        });
    }

    // ---------- 重试失败项（写操作，CLI 安全写 + 护栏） ----------

    private void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "QueueView.BtnRetry_Click", async () =>
        {
            if (_dbPath is null)
            {
                return;
            }
            var root = PathDiscovery.FindMemoryRoot(App.Settings);
            var venvPython = root is null ? null : PathDiscovery.FindVenvPython(root);
            if (root is null || venvPython is null)
            {
                await ShowInfoAsync("无法定位共享 venv（shared-venv\\Scripts\\python.exe）。");
                return;
            }

            if (!await Confirm.AskAsync(
                    this,
                    "重试失败的后台任务",
                    "将把失败的任务重新放回队列，按原参数再处理一次（分批执行，不会一次跑完全部积压）。",
                    "重试会写入记忆库；若失败原因尚未消除（例如密钥失效、网络受限），任务可能再次失败。",
                    "确认重试"))
            {
                return;
            }

            TxtTotal.Text = "正在重试失败任务…";
            // 官方契约：retry-failures 的 --config 必给（否则 exit 2 只打 usage），
            // 且不带 --apply 只是预演（exit 0 但 applied=false，队列并不会被改动）。
            var result = await ProcessRunner.RunAsync(
                venvPython,
                MaintenanceCli.Build(root, "retry-failures", apply: true),
                timeoutMs: 300_000);
            if (result.Success && !MaintenanceCli.IsPreviewOnly(result.StdOut))
            {
                var retried = MaintenanceCli.ReceiptCount(result.StdOut, "retried");
                await ReloadAsync();
                // ReloadAsync 会重写 TxtTotal，故确认信息放在其后拼接，避免被覆盖而看不到。
                TxtTotal.Text = $"已重试：失败任务已重新入队处理（重新入队 {retried?.ToString() ?? "?"} 条）。{TxtTotal.Text}";
            }
            else if (result.Success)
            {
                await ShowInfoAsync("CLI 以退出码 0 结束，但回执显示只做了预演（applied=false），队列未被改动。");
            }
            else
            {
                await ShowInfoAsync($"重试失败：\n{MaintenanceCli.FailureText(result)}");
            }

        });
    }

    private async Task ShowInfoAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "提示",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
