using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScopeRecallWin.Services;

/// <summary>
/// 人工修改记忆的写入通道（唯一合法的"直接写"入口）。
///
/// 上游口径（scope-recall-hermes 3.10.2，已核对
/// adapters/hermes/tool_surface.py 与 contracts/*_request.schema.json）：
///   - 变更工具面 = Hermes adapter 的 MCP 工具 <b>revise</b> / <b>forget</b>（协议 v1.1），
///     经 resident / MCP server 通道调用；<b>上游 CLI 无 mutate 子命令</b>；
///   - <b>无 create 变更操作</b>——claim 只能经 capture 产生，桌面端不提供"新建记忆"写入口；
///   - forget.mode = suppress（停用、仍可按需读取、不可撤销）
///                 | delete（连来源消息及其它事实一并抹除、不可撤销）；
///   - revise.new_value = null 表示撤回（withdraw），旧值留作历史版本；
///   - 身份（host / session / installation）由 provider 绑定，工具参数只带业务字段，
///     桌面端透传用户指令，<b>绝不伪造身份</b>；
///   - 授权语义：核心要求"用户本人最新一条消息明确请求并指明对象"
///     （revise：含新值且指明纠正对象；forget：明确提出并指明条目）。
///     UI 必须令用户亲笔输入，不代笔 / 不预填 / 不记忆复用。
///
/// 禁止裸 SQL 写库（约束 2）。只读查询走 IStorageService（Mode=ReadOnly）。
/// </summary>
public interface IMutateService
{
    /// <summary>校验授权：用户亲笔指令非空且明确点名了目标（不代笔/不预填/不记忆复用）。</summary>
    bool ValidateAuthorization(string userInstruction, string targetRef);

    /// <summary>revise：纠正或撤回一条记忆（写新版本，旧值留历史）。</summary>
    Task<MutateResult> ReviseAsync(ReviseRequest request, string userInstruction);

    /// <summary>forget：停用（suppress）或抹除（delete）记忆；delete 连带来源消息，不可撤销。</summary>
    Task<MutateResult> ForgetAsync(ForgetRequest request, string userInstruction);
}

/// <summary>协议版本（contracts/*.schema.json：protocol_version const "1.1"）。</summary>
public static class MutateProtocol
{
    public const string Version = "1.1";
}

/// <summary>forget.mode 枚举（上游 schema：["suppress", "delete"]）。</summary>
public enum ForgetMode
{
    /// <summary>停用：不再主动使用，仍可按需读取；不可撤销。</summary>
    Suppress,

    /// <summary>抹除：连同来源消息及其它事实一起删除；不可撤销。</summary>
    Delete,
}

/// <summary>
/// revise 请求（contracts/revise_request.schema.json，required 全量）。
/// target_ref / expected_revision 做乐观并发；new_value=null 即撤回。
/// </summary>
public sealed record ReviseRequest(
    string TargetRef,
    long ExpectedRevision,
    JsonElement? NewValue,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> SourceEvidenceRefs,
    DateTimeOffset? ValidFrom);

/// <summary>
/// forget 请求（contracts/forget_request.schema.json）。
/// target_refs 1..100 且唯一；expected_revisions 为 ref→revision 映射；reason 可选（≤1024）。
/// </summary>
public sealed record ForgetRequest(
    IReadOnlyList<string> TargetRefs,
    ForgetMode Mode,
    IReadOnlyDictionary<string, long> ExpectedRevisions,
    string? Reason = null);

/// <summary>写入结果：Ok / Error / 回执 JSON / 审计事件 / 变更摘要。</summary>
public sealed record MutateResult(
    bool Ok, string? Error, string? ReceiptJson, string? AuditEventId, string? Diff);

/// <summary>
/// 写入实现骨架。P2 落实：
///   1. 前置自动备份（store 副本到临时目录，约束 5：更新/迁移/写前必备份）；
///   2. 授权校验（ValidateAuthorization：用户亲笔指令 + 点名目标）；
///   3. 经 resident / MCP server 通道调用 adapter 工具面（协议 v1.1 信封：
///      protocol_version / request_id / 业务字段），身份由 provider 绑定；
///   4. 解析回执（receipt / revision / audit event）；
///   5. 写后回读校验（新值=提交值、版本+1、审计记录存在），不一致提示回滚；
///   6. record_event 留痕 → 审计页可追溯。
/// 危险操作（forget，尤其 delete 连带来源消息、不可撤销）由 UI 二次确认红字（约束 3）。
/// </summary>
public sealed class MutateService : IMutateService
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public bool ValidateAuthorization(string userInstruction, string targetRef)
    {
        // 授权语义对齐上游：用户本人明确提出 + 指明对象；
        // 空指令、未点名目标、与上次完全相同（记忆复用）一律拒绝。
        if (string.IsNullOrWhiteSpace(userInstruction) || string.IsNullOrWhiteSpace(targetRef))
        {
            return false;
        }
        return userInstruction.Contains(targetRef, StringComparison.Ordinal);
    }

    public async Task<MutateResult> ReviseAsync(ReviseRequest request, string userInstruction)
    {
        var invalid = ValidateRevise(request);
        if (invalid is not null)
        {
            return new MutateResult(false, invalid, null, null, null);
        }
        // 授权句已取消（改为界面二次确认）：userInstruction 参数保留以兼容既有调用，不再参与校验。

        // P2：构造信封 → resident/MCP 通道调用 adapter "revise" 工具。
        // var envelope = new Dictionary<string, object?>
        // {
        //     ["protocol_version"] = MutateProtocol.Version,
        //     ["request_id"] = NewRequestId(),
        //     ["target_ref"] = request.TargetRef,
        //     ["expected_revision"] = request.ExpectedRevision,
        //     ["new_value"] = request.NewValue,          // null = 撤回
        //     ["conditions"] = request.Conditions,
        //     ["source_evidence_refs"] = request.SourceEvidenceRefs,  // 1..32 唯一
        //     ["valid_from"] = request.ValidFrom,
        // };
        await Task.CompletedTask;
        return new MutateResult(false, "未实现（P2 接入 Hermes adapter 工具面 revise）", null, null, null);
    }

    public async Task<MutateResult> ForgetAsync(ForgetRequest request, string userInstruction)
    {
        var invalid = ValidateForget(request);
        if (invalid is not null)
        {
            return new MutateResult(false, invalid, null, null, null);
        }
        // 授权句已取消（改为界面二次确认）。

        // P2：构造信封 → resident/MCP 通道调用 adapter "forget" 工具。
        // var envelope = new Dictionary<string, object?>
        // {
        //     ["protocol_version"] = MutateProtocol.Version,
        //     ["request_id"] = NewRequestId(),
        //     ["target_refs"] = request.TargetRefs,              // 1..100 唯一
        //     ["mode"] = request.Mode == ForgetMode.Delete ? "delete" : "suppress",
        //     ["expected_revisions"] = request.ExpectedRevisions,
        //     ["reason"] = request.Reason,                       // 可选，≤1024
        // };
        await Task.CompletedTask;
        return new MutateResult(false, "未实现（P2 接入 Hermes adapter 工具面 forget）", null, null, null);
    }

    /// <summary>按 revise schema 预校验（与上游 strict boundary 同口径，失败早返回）。</summary>
    public static string? ValidateRevise(ReviseRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.TargetRef))
        {
            return "target_ref 必填（1..240 字符）";
        }
        if (r.ExpectedRevision < 1)
        {
            return "expected_revision 必须 ≥ 1";
        }
        if (r.Conditions.Count > 16)
        {
            return "conditions 最多 16 条";
        }
        if (r.SourceEvidenceRefs is { Count: < 1 or > 32 } || r.SourceEvidenceRefs.Distinct().Count() != r.SourceEvidenceRefs.Count)
        {
            return "source_evidence_refs 需 1..32 条且唯一";
        }
        return null;
    }

    /// <summary>按 forget schema 预校验。</summary>
    public static string? ValidateForget(ForgetRequest r)
    {
        if (r.TargetRefs is { Count: < 1 or > 100 } || r.TargetRefs.Distinct().Count() != r.TargetRefs.Count)
        {
            return "target_refs 需 1..100 个且唯一";
        }
        if (r.Mode is not (ForgetMode.Suppress or ForgetMode.Delete))
        {
            return "mode 仅支持 suppress / delete";
        }
        if (r.Reason is { Length: > 1024 })
        {
            return "reason 最长 1024 字符";
        }
        return null;
    }
}
