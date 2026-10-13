using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class MutateView : UserControl
{
    private readonly IMutateService? _mutate;

    public MutateView()
    {
        InitializeComponent();
        _mutate = ServiceLocator.Get<IMutateService>();
    }

    private static List<string> Lines(TextBox box) =>
        (box.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void BtnValidate_Click(object sender, RoutedEventArgs e)
    {
        var isRevise = MutateTabs.SelectedIndex == 0;
        var issues = new List<string>();

        if (isRevise)
        {
            if (!long.TryParse((RevExpectedRevision.Text ?? "").Trim(), out var revision))
            {
                issues.Add("expected_revision 必须是数字");
                revision = 1;
            }

            JsonElement? newValue = null;
            var rawNew = (RevNewValue.Text ?? "").Trim();
            if (rawNew.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawNew);
                    newValue = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    issues.Add($"new_value 不是合法 JSON：{ex.Message}");
                }
            }

            DateTimeOffset? validFrom = null;
            var rawFrom = (RevValidFrom.Text ?? "").Trim();
            if (rawFrom.Length > 0)
            {
                if (DateTimeOffset.TryParse(rawFrom, out var parsed))
                {
                    validFrom = parsed;
                }
                else
                {
                    issues.Add("valid_from 不是合法时间（ISO 8601）");
                }
            }

            var request = new ReviseRequest(
                (RevTargetRef.Text ?? "").Trim(),
                revision,
                newValue,
                Lines(RevConditions),
                Lines(RevEvidenceRefs),
                validFrom);

            var schemaError = MutateService.ValidateRevise(request);
            if (schemaError is not null)
            {
                issues.Add(schemaError);
            }

            var authError = ValidateAuth(request.TargetRef, issues);
            ShowResult(issues, BuildRevisePreview(request, rawNew.Length == 0), authError);
        }
        else
        {
            var refs = Lines(ForTargetRefs);
            var revisions = new Dictionary<string, long>();
            foreach (var line in Lines(ForExpectedRevisions))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && long.TryParse(parts[1].Trim(), out var rev))
                {
                    revisions[parts[0].Trim()] = rev;
                }
                else
                {
                    issues.Add($"expected_revisions 行格式应为 ref=revision：{line}");
                }
            }

            var request = new ForgetRequest(
                refs,
                ForModeDelete.IsChecked == true ? ForgetMode.Delete : ForgetMode.Suppress,
                revisions,
                string.IsNullOrWhiteSpace(ForReason.Text) ? null : ForReason.Text.Trim());

            var schemaError = MutateService.ValidateForget(request);
            if (schemaError is not null)
            {
                issues.Add(schemaError);
            }

            var authError = refs.Count == 0
                ? "授权未通过：target_refs 为空"
                : (refs.Any(r => ValidateAuth(r, new List<string>()) is null) ? null : "授权未通过：授权句需点明至少一个 target_ref");
            ShowResult(issues, BuildForgetPreview(request), authError);
        }
    }

    /// <summary>授权校验（复用 MutateService 口径：非空、≥6 字、点名目标）。</summary>
    private string? ValidateAuth(string targetRef, List<string> issues)
    {
        var auth = (TxtAuthorization.Text ?? "").Trim();
        if (_mutate is null)
        {
            issues.Add("变更服务不可用");
            return null;
        }
        if (auth.Length < 6)
        {
            issues.Add("授权句不足 6 字（需本人书写）");
            return "授权句不足 6 字";
        }
        if (!_mutate.ValidateAuthorization(auth, targetRef))
        {
            issues.Add($"授权句未点明目标「{targetRef}」（不代笔 / 不预填）");
            return "授权句未点明目标";
        }
        return null;
    }

    private void ShowResult(List<string> issues, string preview, string? authError)
    {
        if (issues.Count == 0 && authError is null)
        {
            TxtValidation.Text = "✓ 预校验通过：请求符合上游 schema，授权句已点明目标。";
        }
        else
        {
            var all = new List<string>(issues);
            if (authError is not null && issues.Count == 0)
            {
                all.Add(authError);
            }
            TxtValidation.Text = "✗ 预校验未通过：\n· " + string.Join("\n· ", all);
        }
        TxtEnvelope.Text = preview;
        TxtEnvelope.Visibility = Visibility.Visible;
    }

    private static string BuildRevisePreview(ReviseRequest r, bool withdraw)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["protocol_version"] = MutateProtocol.Version,
            ["tool"] = "revise",
            ["target_ref"] = r.TargetRef,
            ["expected_revision"] = r.ExpectedRevision,
            ["new_value"] = withdraw ? null : r.NewValue?.GetRawText(),
            ["withdraw"] = withdraw,
            ["conditions"] = r.Conditions,
            ["source_evidence_refs"] = r.SourceEvidenceRefs,
            ["valid_from"] = r.ValidFrom?.ToString("O"),
        };
        return "请求信封（协议 v1.1，身份由 provider 绑定，不入参）：\n" +
               JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildForgetPreview(ForgetRequest r)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["protocol_version"] = MutateProtocol.Version,
            ["tool"] = "forget",
            ["target_refs"] = r.TargetRefs,
            ["mode"] = r.Mode == ForgetMode.Delete ? "delete" : "suppress",
            ["expected_revisions"] = r.ExpectedRevisions,
            ["reason"] = r.Reason,
        };
        return "请求信封（协议 v1.1，身份由 provider 绑定，不入参）：\n" +
               JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
    }

    private void BtnExecute_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "MutateView.BtnExecute_Click", async () =>
        {
            var dialog = new ContentDialog
            {
                Title = "写入通道尚未接入",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "本期已实现完整的请求构造与 schema 预校验（见上方信封预览）。\n" +
                           "真实写入需要经 resident / MCP 通道调用 revise / forget 工具面（含 HMAC 握手与回读校验），" +
                           "按约定在下一期接入；在此之前不会向记忆库发出任何写请求。",
                },
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();

        });
    }
}
