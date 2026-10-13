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
        Loaded += QueueView_Loaded;
    }

    private async void QueueView_Loaded(object sender, RoutedEventArgs e)
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
            Table.BindRows(Array.Empty<WorkItemRow>(), Columns());
            return;
        }

        var state = ((ComboBoxItem)CmbState.SelectedItem)?.Content?.ToString();
        if (state == "全部状态")
        {
            state = "all";
        }
        var type = ((ComboBoxItem)CmbType.SelectedItem)?.Content?.ToString();
        if (type == "全部类型")
        {
            type = "all";
        }

        var (rows, total) = await _storage.GetWorkItemsAsync(_dbPath, state, type, 500);
        _rows = rows.ToList();
        TxtTotal.Text = $"共 {total} 条，展示 {_rows.Count} 条（失败/待处理置顶）";
        Table.BindRows(_rows, Columns());
    }

    private static IReadOnlyList<TableColumn> Columns() => new[]
    {
        new TableColumn { Header = "work_id", Width = 210, Value = r => ((WorkItemRow)r).WorkId },
        new TableColumn { Header = "类型", Width = 140, Value = r => ((WorkItemRow)r).WorkType },
        new TableColumn { Header = "状态", Width = 80, Value = r => ((WorkItemRow)r).State },
        new TableColumn { Header = "尝试", Width = 55, Value = r => ((WorkItemRow)r).Attempt.ToString() },
        new TableColumn { Header = "错误码", Width = 150, Value = r => ((WorkItemRow)r).LastErrorCode ?? "" },
        new TableColumn { Header = "subject", Width = 180, Value = r => ((WorkItemRow)r).SubjectRef ?? "" },
        new TableColumn { Header = "可用时间", Width = 150, Value = r => ((WorkItemRow)r).AvailableAt ?? "" },
    };

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void BtnApply_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Table_RowTapped(object? sender, object row)
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
        Field("类型", item.WorkType);
        Field("状态", item.State);
        Field("尝试次数", item.Attempt.ToString());
        Field("subject", item.SubjectRef ?? "—");
        Field("错误码", item.LastErrorCode ?? "—");
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
    }

    // ---------- 重试失败项（写操作，CLI 安全写 + 护栏） ----------

    private async void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_dbPath is null)
        {
            return;
        }
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        var venvPython = root is null ? null : PathDiscovery.FindVenvPython(root);
        if (venvPython is null)
        {
            await ShowInfoAsync("无法定位共享 venv（shared-venv\\Scripts\\python.exe）。");
            return;
        }

        // 授权句 + 确认（红字二次确认）
        var (confirmed, auth) = await ConfirmAsync(
            "重试失败任务",
            "将调用官方 CLI 对失败任务授予一次重新处理机会（retry-failures，有界页面）。\n" +
            "该操作会写入记忆库队列，请确认你本人了解本次操作。",
            "retry-failures");
        if (!confirmed)
        {
            return;
        }

        TxtTotal.Text = "正在执行 retry-failures…";
        var result = await ProcessRunner.RunAsync(
            venvPython,
            "-m scope_recall.maintenance.cli retry-failures",
            timeoutMs: 300_000);
        if (result.Success)
        {
            TxtTotal.Text = $"retry-failures 完成（exit 0）。{(result.StdOut.Length > 200 ? result.StdOut[..200] + "…" : result.StdOut)}";
            await ReloadAsync();
        }
        else
        {
            await ShowInfoAsync($"retry-failures 失败（exit {result.ExitCode}）：\n{FirstLine(result.StdErr) ?? result.StdOut}");
        }
    }

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = text.Split('\n')[0].Trim();
        return line.Length == 0 ? null : line;
    }

    /// <summary>授权句 + 确认对话框；返回 (是否确认, 授权句)。</summary>
    private async Task<(bool Confirmed, string Auth)> ConfirmAsync(string title, string message, string actionVerb)
    {
        var box = new TextBox { PlaceholderText = $"授权句（本人书写，≥6 字）。例：我确认对记忆库执行 {actionVerb}" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
                    box,
                },
            },
            PrimaryButtonText = "确认执行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return (false, "");
        }
        var auth = (box.Text ?? "").Trim();
        if (auth.Length < 6)
        {
            await ShowInfoAsync("授权句不足 6 字，操作已取消。");
            return (false, "");
        }
        return (true, auth);
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
