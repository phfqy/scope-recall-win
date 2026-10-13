using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ScopeRecallWin.Helpers;

/// <summary>
/// 状态/错误的「中文标签 + 语义色 + 英文原名」统一映射。
///
/// 语义色约定（全站一致）：
///   成功/正常/已生效 = 绿；失败/错误 = 红；进行中/待处理 = 蓝；警告/需注意 = 琥珀；未知/未记录 = 灰。
/// 中文标签用于界面显示，英文原名经 <see cref="OriginalTerm"/> 在悬停提示中保留，
/// 便于对照上游文档与日志。
/// </summary>
public static class StatusVisuals
{
    // ---------- 语义色（与 Theme/Dark|Light.xaml 保持一致） ----------

    private static readonly Color Success = Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50);
    private static readonly Color Danger = Color.FromArgb(0xFF, 0xE8, 0x3E, 0x3E);
    private static readonly Color Warning = Color.FromArgb(0xFF, 0xFF, 0xB7, 0x2C);
    private static readonly Color Info = Color.FromArgb(0xFF, 0x4F, 0xC3, 0xF7);
    private static readonly Color Muted = Color.FromArgb(0xFF, 0x9A, 0x9A, 0xA2);

    /// <summary>语义色画笔缓存（SolidColorBrush 必须在 UI 线程创建，缓存避免重复创建与跨线程构造）。</summary>
    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();

    /// <summary>状态/结果 → 语义色画笔。</summary>
    public static Brush BrushOf(string? raw)
    {
        var color = ColorFor(raw);
        if (!BrushCache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            BrushCache[color] = brush;
        }
        return brush;
    }

    /// <summary>状态/结果 → 语义色（纯值，线程无关）。</summary>
    public static Color ColorFor(string? raw)
    {
        var token = Normalize(raw);
        if (SuccessWords.Contains(token))
        {
            return Success;
        }
        if (FailureWords.Contains(token))
        {
            return Danger;
        }
        if (WarningWords.Contains(token))
        {
            return Warning;
        }
        if (InfoWords.Contains(token))
        {
            return Info;
        }
        return Muted;
    }

    /// <summary>是否为失败类状态（用于统计卡/角标着色）。</summary>
    public static bool IsFailure(string? raw) => FailureWords.Contains(Normalize(raw));

    /// <summary>是否为成功类状态。</summary>
    public static bool IsSuccess(string? raw) => SuccessWords.Contains(Normalize(raw));

    // ---------- 候选卡住原因（candidate_lifecycle.reason）人话 ----------

    /// <summary>
    /// 候选为什么停在某个状态/原因（解释「待确认为何没转正」）。
    /// 上游规则见 core/evidence_question.py 与 core/candidate_sweeps.py。
    /// </summary>
    /// <param name="canPromote">true = 还有机会转正；false = 制度性不可转正（只能等休眠归档）。</param>
    public static string CandidateReasonLabel(string processingState, string reason, out bool canPromote)
    {
        var key = $"{Normalize(processingState)}|{Normalize(reason)}";
        canPromote = true;
        switch (key)
        {
            case "waiting_evidence|value_not_in_evidence":
                canPromote = false;
                return "模型改写了原文（常见为译成英文），值无法在来源原文里逐字找到——按规则无法转正，等 30 天无新证据后自动归档";
            case "waiting_evidence|no_authoritative_evidence":
                canPromote = false;
                return "缺少权威来源（需本人或文档的原话），工具输出不算";
            case "waiting_evidence|authority_revoked":
                canPromote = false;
                return "原先授权它的人员/文档已撤回";
            case "waiting_evidence|repeat_without_restatement":
                return "重复出现但没有新的表述，需要新的说法才能转正";
            case "waiting_evidence|evaluated_waiting_evidence":
                return "已评估过一次，仍在等更多证据";
            case "waiting_evidence|evaluation_failed":
                return "上一次评估失败，等下一轮重试";
            case "waiting_evidence|insufficient_evidence":
                return "证据不足，需要更多来源";
            case "waiting_evidence|":
                return "等待更多来源证据";
            case "pending_evaluation|new_evidence":
                return "来了新证据，已排队等待重新评估";
            case "pending_evaluation|operator_retry":
                return "运维重试中（已重新排队）";
            case "pending_evaluation|":
                return "已排队等待评估";
            case "archived|superseded_by_candidate_revision":
                canPromote = false;
                return "已被更新的候选版本取代";
            case "archived|dormant_no_evidence":
                canPromote = false;
                return "长期没有新证据，休眠归档";
            case "resolved|fact_active":
                canPromote = false;
                return "已转正为生效记忆";
            default:
                var label = StateLabels.TryGetValue(reason.Trim(), out var zh) ? zh
                    : StateLabels.TryGetValue(processingState.Trim(), out var zh2) ? zh2
                    : (reason.Length > 0 ? reason : processingState);
                return label;
        }
    }

    // ---------- 中文标签 ----------

    private static readonly Dictionary<string, string> StateLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        // claim 状态
        ["active"] = "已生效",
        ["proposed"] = "待确认",
        ["disputed"] = "有争议",
        ["superseded"] = "已被取代",
        ["retracted"] = "已撤回",
        ["suppressed"] = "已停用",
        // 后台任务
        ["done"] = "已完成",
        ["failed"] = "失败",
        ["pending"] = "待处理",
        ["obsolete"] = "已废弃",
        ["running"] = "进行中",
        ["in_progress"] = "进行中",
        // 候选生命周期
        ["waiting_evidence"] = "待补充证据",
        ["pending_evaluation"] = "待评估",
        ["resolved"] = "已处理",
        ["archived"] = "已归档",
        ["dormant"] = "休眠",
        // 健康/诊断
        ["ok"] = "正常",
        ["degraded"] = "降级",
        ["danger"] = "异常",
        ["warn"] = "需注意",
        ["off"] = "未记录",
        // 部署/链接
        ["alive"] = "在线",
        ["resident"] = "常驻中",
        // 变更模式
        ["suppress"] = "停用",
        ["delete"] = "抹除",
    };

    // ---------- 错误码中文 ----------

    private static readonly Dictionary<string, string> ErrorLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["derivation_retry"] = "来源重试次数过多",
        ["timeout"] = "处理超时",
        ["locked_or_not_replaceable"] = "文件被占用，无法替换",
        ["installed_files_locked_or_not_replaceable"] = "插件文件被占用，请先停止 Agent 后重试",
        ["vector_threshold"] = "相似度阈值不满足",
        ["rate_limited"] = "调用频率受限",
        ["unauthorized"] = "鉴权失败（API Key 无效或缺失）",
        ["network_error"] = "网络不可达",
        ["budget_exceeded"] = "已超出预算上限",
        ["schema_mismatch"] = "数据结构版本不匹配",
    };

    /// <summary>状态 → 中文标签（未收录则原样返回）。</summary>
    public static string StateLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "未记录";
        }
        return StateLabels.TryGetValue(raw.Trim(), out var label) ? label : raw;
    }

    /// <summary>错误码 → 中文说明（支持带后缀的错误码，如 derivation_retry:1|…）。</summary>
    public static string ErrorLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "—";
        }
        var key = raw.Trim();
        if (ErrorLabels.TryGetValue(key, out var label))
        {
            return label;
        }
        // 上游错误码常带后缀（reason:count|detail），按首段再匹配一次
        var head = key.Split(':', '|')[0].Trim();
        if (head.Length > 0 && ErrorLabels.TryGetValue(head, out var headLabel))
        {
            return headLabel;
        }
        return $"{key}（未知错误码）";
    }

    /// <summary>悬停提示：中文标签 + 英文原名（对照上游文档用）。</summary>
    public static string OriginalTerm(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }
        var key = raw.Trim();
        var zh = StateLabels.TryGetValue(key, out var label) ? label
            : ErrorLabels.TryGetValue(key, out var err) ? err
            : null;
        return zh is null ? $"原始值：{key}" : $"{zh}（原始值：{key}）";
    }

    // ---------- 分组 ----------

    private static readonly HashSet<string> SuccessWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "done", "ok", "resolved", "alive", "success", "captured",
    };

    private static readonly HashSet<string> FailureWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed", "error", "danger", "crashed", "missing", "retracted", "unauthorized",
    };

    private static readonly HashSet<string> WarningWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "proposed", "degraded", "warn", "warning", "pending", "waiting_evidence",
        "pending_evaluation", "obsolete", "dormant", "disputed", "timeout", "suppressed",
        "derivation_retry", "rate_limited", "budget_exceeded", "locked_or_not_replaceable",
        "installed_files_locked_or_not_replaceable", "schema_mismatch", "network_error",
    };

    private static readonly HashSet<string> InfoWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "running", "in_progress", "processing", "resident", "embed", "consolidate",
        "evaluate_candidate", "rebuild_projection",
    };

    private static string Normalize(string? raw) => raw?.Trim().ToLowerInvariant() ?? "";
}
