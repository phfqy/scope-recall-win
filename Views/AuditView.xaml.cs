using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class AuditView : UserControl
{
    private readonly IStorageService? _storage;
    private List<DeletionRow> _rows = new();
    private bool _loaded;

    public AuditView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Loaded += AuditView_Loaded;
    }

    private async void AuditView_Loaded(object sender, RoutedEventArgs e)
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
        var db = ResolveDbPath();
        if (db is null || _storage is null)
        {
            TxtTotal.Text = "未找到记忆库：请先在「安装」页选择部署目录。";
            OpList.ItemsSource = null;
            return;
        }

        _rows = (await _storage.GetDeletionsAsync(db, 200)).ToList();
        OpList.ItemsSource = _rows.Select(r => new
        {
            Title = $"{r.OperationId} · mode {r.Mode}",
            Meta = $"时间 {r.CreatedAt ?? "—"} · 对象 {r.RefCount} 个 · 活跃内容移除 {r.ActiveContentRemoved ?? "—"} · epoch {r.MemoryEpoch?.ToString() ?? "—"} · project {r.ProjectId ?? "—"}",
        }).ToList();
        TxtTotal.Text = _rows.Count == 0
            ? "暂无删除操作记录。"
            : $"共 {_rows.Count} 条删除操作（最近 200）。";
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            return;
        }
        var headers = new[] { "operation_id", "mode", "ref_count", "project", "created_at", "epoch", "active_removed" };
        var rows = _rows.Select(r => new[] { r.OperationId, r.Mode, r.RefCount.ToString(), r.ProjectId, r.CreatedAt, r.MemoryEpoch?.ToString(), r.ActiveContentRemoved }).ToList();
        var md = "# 审计 — 删除操作台账\n\n" + Exporter.ToMarkdown(headers, rows);
        await Exporter.SaveWithPickerAsync("audit-deletions.md", md, ".md", "Markdown");
    }
}
