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

    public OverviewView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Loaded += (_, _) => UiThread.Run(DispatcherQueue, "OverviewView.Loaded", ReloadAsync);
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "OverviewView.Refresh", ReloadAsync);

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
            TxtSourceBadge.Text = "未连接（未找到记忆库）";
            SourceBadge.Background = (Brush)Application.Current.Resources["DangerSoftBrush"];
            TxtHealthScore.Text = "—";
            TxtHealthTitle.Text = "还没有找到记忆库";
            TxtHealthSummary.Text = "请先到「安装」页选择部署目录，或确认记忆库是否已部署在本机。";
            TxtClaimTotal.Text = "—";
            TxtWorkTotal.Text = "—";
            TxtCandidateWaiting.Text = "—";
            TxtDbSize.Text = "—";
            return;
        }

        var stats = await _storage.GetOverviewStatsAsync(_dbPath);
        var breakdown = _dbPath is null
            ? new List<CandidateReasonRow>()
            : (await _storage.GetCandidateBreakdownAsync(_dbPath)).ToList();
        if (stats is null)
        {
            TxtSourceBadge.Text = "读取失败（记忆库无法打开）";
            SourceBadge.Background = (Brush)Application.Current.Resources["DangerSoftBrush"];
            TxtHealthTitle.Text = "读取失败";
            TxtHealthSummary.Text = "记忆库文件无法打开，可能正在被其他程序独占。请稍后重试。";
            return;
        }

        TxtSourceBadge.Text = $"已连接 · 数据结构 v{stats.SchemaVersion} · 记忆版本 {stats.MemoryEpoch}";
        SourceBadge.Background = (Brush)Application.Current.Resources["SuccessSoftBrush"];

        // 四个关键数字 + 人话注解
        TxtClaimTotal.Text = $"{stats.ClaimTotal} 条";
        TxtClaimHint.Text = $"其中 {stats.ClaimActive} 条已生效、{stats.ClaimProposed} 条待确认";

        var workTotal = stats.WorkDone + stats.WorkFailed + stats.WorkPending;
        TxtWorkTotal.Text = $"{workTotal} 个";
        TxtWorkHint.Text = stats.WorkFailed > 0
            ? $"失败 {stats.WorkFailed} 个（可到「队列」页重试）、待处理 {stats.WorkPending} 个"
            : $"全部处理完成（成功 {stats.WorkDone}、待处理 {stats.WorkPending}）";
        TxtWorkHint.Foreground = stats.WorkFailed > 0
            ? StatusVisuals.BrushOf("failed")
            : (Brush)Application.Current.Resources["MutedTextBrush"];

        // 卡片大数与下方明细用同一口径（全部待确认候选），否则会出现「数字对不上明细」
        var candidateTotal = breakdown.Count > 0 ? breakdown.Sum(row => row.Count) : stats.CandidateWaiting;
        TxtCandidateWaiting.Text = $"{candidateTotal} 条";
        TxtCandidateHint.Text = CandidateHintText(breakdown);

        TxtDbSize.Text = Exporter.HumanSize(stats.DbSizeBytes);
        TxtDbHint.Text = "记忆库文件当前大小";

        // 更多信息
        TxtEventTotal.Text = $"{stats.EventTotal} 条";
        TxtNewestEvent.Text = stats.NewestEventAt is { } newest ? FormatRelative(newest) : "未记录";
        TxtDataDir.Text = stats.DataDirectory ?? "未记录";
        TxtSchema.Text = $"v{stats.SchemaVersion} · 记忆版本 {stats.MemoryEpoch}";

        var items = BuildHealthItems(stats, breakdown);
        RenderHealth(items, stats);
        RenderAttention(stats, breakdown);
        _ = RefreshPluginVersionAsync(items, stats);
    }

    /// <summary>
    /// 把「待确认为何没转正」翻译成人话：按数量倒序列出前几档，可转正与不可转正分开说。
    /// </summary>
    private static string CandidateHintText(IReadOnlyList<CandidateReasonRow> breakdown)
    {
        if (breakdown.Count == 0)
        {
            return "还需要更多来源证据才能转正为记忆";
        }
        var total = breakdown.Sum(row => row.Count);
        var lines = new List<string>();
        long blocked = 0;
        foreach (var row in breakdown.Take(4))
        {
            var label = StatusVisuals.CandidateReasonLabel(row.ProcessingState, row.Reason, out var canPromote);
            if (!canPromote)
            {
                blocked += row.Count;
            }
            lines.Add($"• {row.Count} 条：{label}");
        }
        var hidden = total - breakdown.Take(4).Sum(row => row.Count);
        if (hidden > 0)
        {
            lines.Add($"• 其余 {hidden} 条：其它原因");
        }
        if (blocked > 0)
        {
            lines.Add($"→ 其中 {blocked} 条按现行规则无法转正（上游提示词已要求逐字、但模型未遵守），" +
                      "只能等 30 天休眠归档或由上游修复后重评。");
        }
        return string.Join("\n", lines);
    }

    private static string FormatRelative(DateTimeOffset ts)
    {
        var local = ts.ToLocalTime();
        var span = DateTimeOffset.Now - local;
        if (span.TotalMinutes < 1)
        {
            return "刚刚";
        }
        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes} 分钟前（{local:HH:mm}）";
        }
        if (span.TotalDays < 1)
        {
            return $"{(int)span.TotalHours} 小时前（{local:HH:mm}）";
        }
        return $"{(int)span.TotalDays} 天前（{local:yyyy-MM-dd}）";
    }

    // ---------- 健康分 ----------

    private static List<HealthItem> BuildHealthItems(
        OverviewStats stats, IReadOnlyList<CandidateReasonRow> breakdown)
    {
        var items = new List<HealthItem>();

        var proposedRatio = stats.ClaimTotal == 0 ? 0 : (double)stats.ClaimProposed / stats.ClaimTotal;
        var blockedCount = breakdown.Sum(row => CanPromote(row) ? 0L : row.Count);
        var blockedDetail = blockedCount > 0
            ? $"，其中 {blockedCount} 条按规则无法转正（模型改写了原文，等 30 天休眠归档）"
            : "";
        items.Add(new HealthItem(
            "待确认记忆",
            $"{stats.ClaimProposed} / {stats.ClaimTotal} 条（{proposedRatio:P0}）尚未转正{blockedDetail}",
            proposedRatio > 0.8 ? HealthState.Danger : proposedRatio > 0.5 ? HealthState.Warn : HealthState.Ok));

        items.Add(new HealthItem(
            "后台任务失败",
            stats.WorkFailed > 0 ? $"{stats.WorkFailed} 个任务失败，可在「队列」页重试" : "没有失败任务",
            stats.WorkFailed > 20 ? HealthState.Danger : stats.WorkFailed > 0 ? HealthState.Warn : HealthState.Ok));

        items.Add(new HealthItem(
            "候选等待证据",
            blockedCount > 0
                ? $"{CandidateTotal(stats, breakdown)} 条候选等待证据，其中 {blockedCount} 条按规则无法转正（只能等休眠归档）"
                : CandidateTotal(stats, breakdown) > 100
                    ? $"{CandidateTotal(stats, breakdown)} 条候选仍在等待更多来源证据"
                    : $"{CandidateTotal(stats, breakdown)} 条候选等待证据",
            stats.CandidateWaiting > 400 ? HealthState.Danger : stats.CandidateWaiting > 100 ? HealthState.Warn : HealthState.Ok));

        var sizeMb = stats.DbSizeBytes / (1024 * 1024);
        items.Add(new HealthItem(
            "记忆库体积",
            $"{Exporter.HumanSize(stats.DbSizeBytes)}" + (sizeMb > 512 ? "（偏大，可考虑归档）" : "（正常）"),
            sizeMb > 512 ? HealthState.Warn : HealthState.Ok));

        items.Add(new HealthItem("插件版本", "正在检查是否有新版本…", HealthState.Off));
        return items;
    }

    /// <summary>该候选按现行规则是否还有转正机会（用于区分「能等」与「永远等不到」）。</summary>
    private static bool CanPromote(CandidateReasonRow row)
    {
        StatusVisuals.CandidateReasonLabel(row.ProcessingState, row.Reason, out var canPromote);
        return canPromote;
    }

    /// <summary>待确认候选总数（与明细卡同一口径：有明细用明细合计，否则退回 waiting_evidence 计数）。</summary>
    private static long CandidateTotal(OverviewStats stats, IReadOnlyList<CandidateReasonRow> breakdown) =>
        breakdown.Count > 0 ? breakdown.Sum(row => row.Count) : stats.CandidateWaiting;

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

    /// <summary>渲染健康分：分数 + 一句话结论（人话）+ 明细列表。</summary>
    private void RenderHealth(List<HealthItem> items, OverviewStats? stats)
    {
        var score = ComputeScore(items);
        TxtHealthScore.Text = score.ToString();
        HealthBar.Value = score;

        var (title, tint, conclusion) = score >= 80
            ? ("记忆健康：状态良好", "ok", "记忆库运行正常，没有需要立即处理的问题。")
            : score >= 60
                ? ("记忆健康：需要注意", "pending", BuildConclusion(items, "有以下几项建议处理："))
                : ("记忆健康：需要处理", "failed", BuildConclusion(items, "有以下问题需要尽快处理："));

        TxtHealthTitle.Text = title;
        TxtHealthTitle.Foreground = StatusVisuals.BrushOf(tint);
        TxtHealthScore.Foreground = StatusVisuals.BrushOf(tint);
        TxtHealthSummary.Text = conclusion;
        HealthBar.Foreground = StatusVisuals.BrushOf(tint);

        HealthList.Children.Clear();
        foreach (var item in items)
        {
            HealthList.Children.Add(BuildHealthRow(item));
        }
    }

    private static string BuildConclusion(List<HealthItem> items, string prefix)
    {
        var problems = new List<string>();
        foreach (var item in items)
        {
            if (item.State is HealthState.Warn or HealthState.Danger)
            {
                // 只取“数字 + 名称”这一小段，避免长句堆叠
                problems.Add(item.Name + "——" + item.Detail);
            }
        }
        return problems.Count == 0 ? "没有需要处理的问题。" : prefix + " " + string.Join("；", problems);
    }

    private static Grid BuildHealthRow(HealthItem item)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var led = Led(item.State);
        Grid.SetColumn(led, 0);
        row.Children.Add(led);

        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var detail = new TextBlock
        {
            Text = item.Detail,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = item.State == HealthState.Ok
                ? (Brush)Application.Current.Resources["MutedTextBrush"]
                : StatusVisuals.BrushOf(item.State == HealthState.Danger ? "failed" : "pending"),
        };
        Grid.SetColumn(detail, 2);
        row.Children.Add(detail);
        return row;
    }

    private async System.Threading.Tasks.Task RefreshPluginVersionAsync(List<HealthItem> items, OverviewStats stats)
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
        var detail = "未能检查（网络不可用）";
        if (check.Error is null && check.InstalledVersion is not null && check.LatestVersion is not null)
        {
            if (UpdateService.IsNewerThan(check.LatestVersion, check.InstalledVersion))
            {
                state = HealthState.Warn;
                detail = $"已安装 {check.InstalledVersion}，可升级到 {check.LatestVersion}（到「更新」页操作）";
            }
            else
            {
                state = HealthState.Ok;
                detail = $"已是最新版本 {check.InstalledVersion}";
            }
        }
        items[index] = new HealthItem("插件版本", detail, state);
        RenderHealth(items, stats);
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

    private void RenderAttention(OverviewStats stats, IReadOnlyList<CandidateReasonRow> breakdown)
    {
        AttentionList.Children.Clear();

        void Add(string text, BadgeState state) => AttentionList.Children.Add(new StatBadge { Text = text, State = state });

        if (stats.WorkFailed > 0)
        {
            Add($"有 {stats.WorkFailed} 个后台任务失败，可在「队列」页一键重试", BadgeState.Danger);
        }
        var blockedCount = breakdown.Sum(row => CanPromote(row) ? 0L : row.Count);
        if (blockedCount > 0)
        {
            Add($"{blockedCount} 条待确认候选因「模型改写原文、值无法逐字溯源」无法转正，" +
                "将随 30 天休眠归档清理；根治需上游在整合时强制逐字取值", BadgeState.Warn);
        }
        if (stats.ClaimProposed > 100)
        {
            Add($"有 {stats.ClaimProposed} 条记忆等待确认（候选尚未转正）", BadgeState.Warn);
        }
        if (AttentionList.Children.Count == 0)
        {
            AttentionList.Children.Add(new TextBlock
            {
                Text = "暂无需要关注的事项，一切正常。",
                FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            });
        }
    }
}
