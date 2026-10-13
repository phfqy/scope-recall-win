using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScopeRecallWin.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;
using Windows.UI;

namespace ScopeRecallWin.Views;

public sealed partial class OverviewView : UserControl
{
    private readonly IStorageService? _storage;
    private string? _dbPath;
    private bool _loaded;

    public OverviewView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Loaded += OverviewView_Loaded;
    }

    private async void OverviewView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await ReloadAsync();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
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

    private async System.Threading.Tasks.Task ReloadAsync()
    {
        _dbPath = ResolveDbPath();
        if (_dbPath is null || _storage is null)
        {
            TxtSourceBadge.Text = "未连接：未找到记忆库";
            SourceBadge.Background = (Brush)Application.Current.Resources["DangerSoftBrush"];
            TxtClaimTotal.Text = "—";
            TxtEventTotal.Text = "—";
            TxtDbSize.Text = "—";
            TxtClaimSplit.Text = "请先在「安装」页选择部署目录";
            return;
        }

        var stats = await _storage.GetOverviewStatsAsync(_dbPath);
        if (stats is null)
        {
            TxtSourceBadge.Text = "已断线：库打开失败";
            SourceBadge.Background = (Brush)Application.Current.Resources["DangerSoftBrush"];
            return;
        }

        TxtSourceBadge.Text = $"live · schema {stats.SchemaVersion} · epoch {stats.MemoryEpoch}";
        SourceBadge.Background = (Brush)Application.Current.Resources["SuccessSoftBrush"];

        TxtClaimTotal.Text = stats.ClaimTotal.ToString();
        TxtClaimSplit.Text = $"active {stats.ClaimActive} · proposed {stats.ClaimProposed}";
        TxtEventTotal.Text = stats.EventTotal.ToString();
        TxtNewestEvent.Text = stats.NewestEventAt is { } newest
            ? $"最近 {newstLocal(newest)}"
            : "—";
        TxtDbSize.Text = Exporter.HumanSize(stats.DbSizeBytes);
        TxtSchema.Text = $"schema_version {stats.SchemaVersion}";
        TxtCandidateWaiting.Text = stats.CandidateWaiting.ToString();
        TxtWorkTotal.Text = (stats.WorkDone + stats.WorkFailed + stats.WorkPending).ToString();
        TxtWorkSplit.Text = $"done {stats.WorkDone} · failed {stats.WorkFailed} · pending {stats.WorkPending}";
        TxtDataDir.Text = stats.DataDirectory ?? "—";
        TxtAgentId.Text = $"agent_id {stats.AgentId ?? "—"}";

        var items = BuildHealthItems(stats);
        RenderHealthRows(items);
        RenderAttention(stats);
        _ = RefreshPluginVersionAsync(items);
    }

    private static string newstLocal(DateTimeOffset ts)
    {
        var local = ts.ToLocalTime();
        var days = (DateTimeOffset.Now - local).TotalDays;
        return days < 1 ? $"今天 {local:HH:mm}" : $"{local:yyyy-MM-dd}";
    }

    // ---------- 健康分（WebUI 五扣分项口径） ----------

    private static List<HealthItem> BuildHealthItems(OverviewStats stats)
    {
        var items = new List<HealthItem>();

        var proposedRatio = stats.ClaimTotal == 0 ? 0 : (double)stats.ClaimProposed / stats.ClaimTotal;
        items.Add(new HealthItem(
            "proposed 积压",
            $"{stats.ClaimProposed} / {stats.ClaimTotal}（{proposedRatio:P0}）",
            proposedRatio > 0.8 ? HealthState.Danger : proposedRatio > 0.5 ? HealthState.Warn : HealthState.Ok));

        items.Add(new HealthItem(
            "失败任务",
            $"{stats.WorkFailed} 条",
            stats.WorkFailed > 20 ? HealthState.Danger : stats.WorkFailed > 0 ? HealthState.Warn : HealthState.Ok));

        items.Add(new HealthItem(
            "候选等待证据",
            $"{stats.CandidateWaiting} 条",
            stats.CandidateWaiting > 400 ? HealthState.Danger : stats.CandidateWaiting > 100 ? HealthState.Warn : HealthState.Ok));

        var sizeMb = stats.DbSizeBytes / (1024 * 1024);
        items.Add(new HealthItem(
            "存储体积",
            Exporter.HumanSize(stats.DbSizeBytes),
            sizeMb > 512 ? HealthState.Warn : HealthState.Ok));

        items.Add(new HealthItem("插件版本", "检查中…", HealthState.Off));
        return items;
    }

    private static int ComputeScore(List<HealthItem> items)
    {
        var score = 100;
        foreach (var item in items)
        {
            switch (item.State)
            {
                case HealthState.Danger: score -= 15; break;
                case HealthState.Warn: score -= 7; break;
            }
        }
        return Math.Max(0, score);
    }

    private void RenderHealthRows(List<HealthItem> items)
    {
        var score = ComputeScore(items);
        TxtHealthScore.Text = score.ToString();
        HealthBar.Value = score;
        HealthBar.Foreground = score >= 80
            ? (Brush)Application.Current.Resources["SuccessBrush"]
            : score >= 60
                ? (Brush)Application.Current.Resources["WarningBrush"]
                : (Brush)Application.Current.Resources["DangerBrush"];

        HealthList.Children.Clear();
        foreach (var item in items)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var led = Led(item.State);
            Grid.SetColumn(led, 0);
            row.Children.Add(led);

            var name = new TextBlock
            {
                Text = item.Name,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var detail = new TextBlock
            {
                Text = item.Detail,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            };
            Grid.SetColumn(detail, 2);
            row.Children.Add(detail);

            HealthList.Children.Add(row);
        }
    }

    private async System.Threading.Tasks.Task RefreshPluginVersionAsync(List<HealthItem> items)
    {
        var plugin = ServiceLocator.Get<IPluginService>();
        if (plugin is null)
        {
            return;
        }
        var check = await plugin.CheckUpstreamAsync();
        var index = items.FindIndex(i => i.Name == "插件版本");
        if (index < 0)
        {
            return;
        }
        var state = HealthState.Off;
        var detail = "待接入";
        if (check.Error is null && check.InstalledVersion is not null && check.LatestVersion is not null)
        {
            if (UpdateService.IsNewerThan(check.LatestVersion, check.InstalledVersion))
            {
                state = HealthState.Warn;
                detail = $"{check.InstalledVersion} → {check.LatestVersion} 可升级";
            }
            else
            {
                state = HealthState.Ok;
                detail = $"{check.InstalledVersion}（最新）";
            }
        }
        items[index] = new HealthItem("插件版本", detail, state);
        RenderHealthRows(items);
    }

    private static Border Led(HealthState state)
    {
        var color = state switch
        {
            HealthState.Ok => Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50),
            HealthState.Warn => Color.FromArgb(0xFF, 0xFF, 0xB7, 0x2C),
            HealthState.Danger => Color.FromArgb(0xFF, 0xE8, 0x3E, 0x3E),
            _ => Color.FromArgb(0xFF, 0x8A, 0x8A, 0x92),
        };
        return new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // ---------- 需要关注 ----------

    private void RenderAttention(OverviewStats stats)
    {
        AttentionList.Children.Clear();

        void AddBadge(string text, BadgeState state)
        {
            AttentionList.Children.Add(new StatBadge { Text = text, State = state });
        }

        if (stats.ClaimProposed > 100)
        {
            AddBadge($"proposed 积压 {stats.ClaimProposed} 条（候选未转正）", BadgeState.Warn);
        }
        if (stats.WorkFailed > 0)
        {
            AddBadge($"失败任务 {stats.WorkFailed} 条（队列页可重试）", BadgeState.Danger);
        }
        if (stats.CandidateWaiting > 100)
        {
            AddBadge($"候选等待证据 {stats.CandidateWaiting} 条", BadgeState.Info);
        }
        if (AttentionList.Children.Count == 0)
        {
            AttentionList.Children.Add(new TextBlock
            {
                Text = "暂无需要关注的事项。",
                FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            });
        }
    }
}
