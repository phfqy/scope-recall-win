using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class AiInfoView : UserControl
{
    private readonly IAgentLinkService? _links;
    private readonly IStorageService? _storage;
    private bool _loaded;

    public AiInfoView()
    {
        InitializeComponent();
        _links = ServiceLocator.Get<IAgentLinkService>();
        _storage = ServiceLocator.Get<IStorageService>();
        Loaded += AiInfoView_Loaded;
    }

    private async void AiInfoView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await ReloadAsync();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        TxtSummary.Text = "正在探测 Agent 链接…";
        LinkList.ItemsSource = null;

        if (_links is null)
        {
            TxtSummary.Text = "Agent 链接服务不可用。";
            return;
        }

        var statuses = (await _links.ProbeAsync()).ToList();
        if (statuses.Count == 0)
        {
            TxtSummary.Text = "未找到记忆库或 installation.json 中没有 entry。";
            return;
        }

        LinkList.ItemsSource = statuses.Select(s => new
        {
            Title = $"{s.Entry} · {s.Host}",
            Health = s.Health switch
            {
                "ok" => "● 已连接",
                "warn" => "● 部分可用",
                _ => "● 未连接",
            },
            Detail = $"{s.HealthDetail}\n" +
                     $"配置：{s.ConfigDetail}\n" +
                     $"resident：{(s.ResidentAlive ? $"运行中 pid {s.Pid} · 端口 {s.Port} · v{s.ResidentVersion}" : "未运行")}\n" +
                     $"home：{s.Home}\n" +
                     $"库内最后活跃：{s.LastSeen ?? "未记录"}",
        }).ToList();

        var okCount = statuses.Count(s => s.Health == "ok");
        TxtSummary.Text = $"共 {statuses.Count} 个 entry：已连接 {okCount} · 部分可用 {statuses.Count(s => s.Health == "warn")} · 未连接 {statuses.Count(s => s.Health == "danger")}";

        await LoadWorkStatsAsync();
    }

    private async Task LoadWorkStatsAsync()
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null || _storage is null)
        {
            TxtWorkStats.Text = "未找到记忆库。";
            return;
        }
        var db = PathDiscovery.MemoryDbPath(root);
        if (!File.Exists(db))
        {
            TxtWorkStats.Text = "未找到 memory.sqlite3。";
            return;
        }

        var stats = await _storage.GetOverviewStatsAsync(db);
        if (stats is null)
        {
            TxtWorkStats.Text = "统计查询失败。";
            return;
        }

        TxtWorkStats.Text =
            $"work_items：done {stats.WorkDone} · failed {stats.WorkFailed} · pending {stats.WorkPending}\n" +
            $"candidate 等待证据：{stats.CandidateWaiting}\n" +
            $"记忆事件：{stats.EventTotal} · claim：{stats.ClaimTotal}（active {stats.ClaimActive}）\n" +
            $"memory_epoch：{stats.MemoryEpoch} · schema：{stats.SchemaVersion}";
    }
}
