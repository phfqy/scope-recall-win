using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;
using Windows.System;

namespace ScopeRecallWin.Views;

public sealed partial class MutateView : UserControl
{
    /// <summary>记忆列表行。</summary>
    private sealed record ClaimPick(string ClaimId, string Subject, string Predicate, string State, long Revision, string Head, string Sub);

    private readonly IStorageService? _storage;
    private readonly IMutateService? _mutate;
    private string? _dbPath;
    private List<ClaimPick> _picks = new();
    private ClaimPick? _selected;

    public MutateView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        _mutate = ServiceLocator.Get<IMutateService>();
        Loaded += (_, _) => UiThread.Run(DispatcherQueue, "MutateView.Loaded", LoadAsync);
    }

    private async Task LoadAsync()
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        var path = root is null ? null : PathDiscovery.MemoryDbPath(root);
        _dbPath = path is not null && File.Exists(path) ? path : null;
        if (_dbPath is null)
        {
            TxtState.Text = "未找到记忆库：请先到「安装」页选择部署目录。";
            TxtState.Foreground = StatusVisuals.BrushOf("failed");
            return;
        }
        await SearchAsync();
    }

    // ---------- 第 1 步：搜索并选择 ----------

    private void TxtSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            UiThread.Run(DispatcherQueue, "MutateView.Search", SearchAsync);
        }
    }

    private void BtnSearch_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "MutateView.Search", SearchAsync);

    private async Task SearchAsync()
    {
        if (_dbPath is null || _storage is null)
        {
            return;
        }
        var keyword = (TxtSearch.Text ?? "").Trim();
        var (rows, total) = await _storage.GetClaimsAsync(
            _dbPath,
            "all",
            keyword.Length == 0 ? null : keyword,
            1,
            50);

        _picks = rows.Select(c => new ClaimPick(
            c.ClaimId,
            c.Subject,
            c.Predicate,
            c.State,
            c.CurrentRevision,
            $"{c.Subject} · {c.Predicate}",
            $"{StatusVisuals.StateLabel(c.State)} · 版本 {c.CurrentRevision} · {c.ClaimId}")).ToList();

        ClaimList.ItemsSource = _picks;
        TxtSelected.Text = _picks.Count == 0
            ? "没有找到匹配的记忆，换个关键词试试。"
            : $"找到 {_picks.Count} 条（共 {total} 条符合条件），点击其中一条进行选择。";
        TxtSelected.Foreground = (Brush)Application.Current.Resources["MutedTextBrush"];
        _selected = null;
        RefreshPreview();
    }

    private void ClaimList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ClaimPick pick)
        {
            return;
        }
        _selected = pick;
        TxtSelected.Text = $"已选择：{pick.Subject} · {pick.Predicate}（{StatusVisuals.StateLabel(pick.State)}，版本 {pick.Revision}）";
        TxtSelected.Foreground = StatusVisuals.BrushOf("ok");
        UiThread.Run(DispatcherQueue, "MutateView.RefreshPreview", RefreshPreviewAsync);
    }

    // ---------- 第 2 步：动作 ----------

    private void Action_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtNewValue is null)
        {
            return;
        }
        var isRevise = RadioRevise.IsChecked == true;
        TxtNewValue.Visibility = isRevise ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
    }

    private string ActionName() =>
        RadioWithdraw.IsChecked == true ? "撤回"
        : RadioSuppress.IsChecked == true ? "停用"
        : RadioDelete.IsChecked == true ? "删除"
        : "改内容";

    // ---------- 第 3 步：预览 / 执行 ----------

    private async Task RefreshPreviewAsync()
    {
        if (_selected is null || _dbPath is null || _storage is null)
        {
            RefreshPreview();
            return;
        }
        var versions = await _storage.GetClaimVersionsAsync(_dbPath, _selected.ClaimId);
        var current = versions.FirstOrDefault(v => v.Revision == _selected.Revision) ?? versions.LastOrDefault();
        _currentPayload = current?.PayloadJson;
        RefreshPreview();
    }

    private string? _currentPayload;

    private void RefreshPreview()
    {
        if (_selected is null)
        {
            TxtPreviewTitle.Text = "尚未选择记忆";
            TxtPreviewBody.Text = "请先在第 1 步点击一条记忆。";
            return;
        }

        var action = ActionName();
        TxtPreviewTitle.Text = $"将对「{_selected.Subject} · {_selected.Predicate}」执行：{action}";

        var body = new List<string>
        {
            $"记忆编号：{_selected.ClaimId}（当前版本 {_selected.Revision}）",
            $"当前状态：{StatusVisuals.StateLabel(_selected.State)}",
        };
        if (_currentPayload is { Length: > 0 })
        {
            body.Add($"当前内容：{Shorten(_currentPayload, 180)}");
        }

        body.Add(action switch
        {
            "改内容" => (TxtNewValue.Text ?? "").Trim().Length == 0
                ? "新的内容：（还没填写）"
                : $"新的内容：{Shorten(TxtNewValue.Text!.Trim(), 180)}",
            "撤回" => "结果：这条记忆被撤回，历史版本保留，记忆不再被使用。",
            "停用" => "结果：这条记忆暂时不再被使用，数据保留，可再次启用。",
            _ => "结果：这条记忆连同它的来源消息一起被抹除，无法恢复。",
        });
        TxtPreviewBody.Text = string.Join("\n", body);
    }

    private static string Shorten(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max] + "…";

    private void BtnPreviewEnvelope_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            TxtState.Text = "请先选择一条记忆。";
            TxtState.Foreground = StatusVisuals.BrushOf("pending");
            return;
        }
        TxtEnvelope.Text = BuildEnvelopeJson();
        AdvExpander.Visibility = Visibility.Visible;
        AdvExpander.IsExpanded = true;
    }

    /// <summary>构造协议信封（技术细节展示用）。</summary>
    private string BuildEnvelopeJson()
    {
        if (_selected is null)
        {
            return "";
        }
        var action = ActionName();
        var isDelete = action == "删除";
        var isRevise = action == "改内容";

        var envelope = new Dictionary<string, object?>
        {
            ["protocol_version"] = MutateProtocol.Version,
            ["tool"] = isRevise || action == "撤回" ? "revise" : "forget",
            ["target_ref"] = _selected.ClaimId,
            ["expected_revision"] = _selected.Revision,
            ["action"] = action,
            ["new_value"] = isRevise ? (TxtNewValue.Text ?? "").Trim() : null,
            ["mode"] = isRevise ? null : (isDelete ? "delete" : "suppress"),
            ["reason"] = string.IsNullOrWhiteSpace(TxtReason.Text) ? null : TxtReason.Text!.Trim(),
        };
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
    }

    private void BtnExecute_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "MutateView.Execute", async () =>
        {
            if (_selected is null)
            {
                TxtState.Text = "请先在第 1 步选择一条记忆。";
                TxtState.Foreground = StatusVisuals.BrushOf("pending");
                return;
            }
            var action = ActionName();
            if (action == "改内容" && (TxtNewValue.Text ?? "").Trim().Length == 0)
            {
                TxtState.Text = "请先填写「新的内容」。";
                TxtState.Foreground = StatusVisuals.BrushOf("pending");
                return;
            }

            var consequence = action switch
            {
                "删除" => "删除会连同来源消息一起抹除，无法恢复；请确认这是你要的结果。",
                "撤回" => "撤回后这条记忆不再被使用（历史版本仍保留）。",
                "停用" => "停用后这条记忆暂时不再被使用，数据保留。",
                _ => "将以新版本覆盖当前说法，旧说法保留在历史中。",
            };
            if (!await Confirm.AskAsync(
                    this,
                    $"确认{action}这条记忆",
                    $"记忆：{_selected.Subject} · {_selected.Predicate}（{_selected.ClaimId}）",
                    consequence,
                    $"确认{action}"))
            {
                return;
            }

            TxtState.Text = "正在提交变更…";
            TxtState.Foreground = StatusVisuals.BrushOf("pending");
            TxtEnvelope.Text = BuildEnvelopeJson();
            AdvExpander.Visibility = Visibility.Visible;

            var result = await ExecuteAsync(action);
            if (result.Ok)
            {
                TxtState.Text = "✓ 变更已提交。";
                TxtState.Foreground = StatusVisuals.BrushOf("ok");
            }
            else
            {
                TxtState.Text = $"✗ 未能完成：{result.Error}";
                TxtState.Foreground = StatusVisuals.BrushOf(result.Error is not null && result.Error.Contains("未实现") ? "pending" : "failed");
            }
        });

    /// <summary>按动作类型调用变更服务。</summary>
    private async Task<MutateResult> ExecuteAsync(string action)
    {
        if (_mutate is null || _selected is null)
        {
            return new MutateResult(false, "变更服务不可用。", null, null, null);
        }

        var reason = string.IsNullOrWhiteSpace(TxtReason.Text) ? null : TxtReason.Text!.Trim();
        const string confirmed = "二次确认";

        switch (action)
        {
            case "改内容":
            case "撤回":
            {
                JsonElement? newValue = null;
                if (action == "改内容")
                {
                    var raw = (TxtNewValue.Text ?? "").Trim();
                    var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["value_text"] = raw });
                    using var doc = JsonDocument.Parse(json);
                    newValue = doc.RootElement.Clone();
                }
                var request = new ReviseRequest(
                    _selected.ClaimId,
                    _selected.Revision,
                    newValue,
                    Array.Empty<string>(),
                    new[] { _selected.ClaimId },
                    null);
                return await _mutate.ReviseAsync(request, confirmed);
            }
            default:
            {
                var mode = action == "删除" ? ForgetMode.Delete : ForgetMode.Suppress;
                var request = new ForgetRequest(
                    new[] { _selected.ClaimId },
                    mode,
                    new Dictionary<string, long> { [_selected.ClaimId] = _selected.Revision },
                    reason);
                return await _mutate.ForgetAsync(request, confirmed);
            }
        }
    }
}
