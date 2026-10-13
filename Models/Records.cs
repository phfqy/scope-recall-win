namespace ScopeRecallWin.Models;

// ---------- 总览 ----------

/// <summary>总览统计（sqlite 只读聚合）。</summary>
public sealed record OverviewStats(
    long ClaimTotal,
    long ClaimActive,
    long ClaimProposed,
    long EventTotal,
    long CandidateWaiting,
    long WorkFailed,
    long WorkPending,
    long WorkDone,
    double DbSizeBytes,
    int SchemaVersion,
    long MemoryEpoch,
    string? AgentId,
    string? DataDirectory,
    DateTimeOffset? NewestEventAt);

/// <summary>健康分扣分项。</summary>
public sealed record HealthItem(string Name, string Detail, HealthState State);

public enum HealthState { Ok, Warn, Danger, Off }

// ---------- 记忆 ----------

/// <summary>claim 列表行。</summary>
public sealed record ClaimRow(
    string ClaimId,
    string Subject,
    string Predicate,
    string Kind,
    string State,
    int CurrentRevision,
    int VersionCount,
    int EvidenceCount,
    bool Suppressed);

/// <summary>claim 版本行。</summary>
public sealed record ClaimVersionRow(
    int Revision,
    string State,
    string? Basis,
    string? QualificationReason,
    string? ValidFrom,
    string? ValidTo,
    string? RecordedFrom,
    string PayloadJson,
    string? ReplacesRevision);

/// <summary>证据行。</summary>
public sealed record EvidenceRow(
    string SourceRef,
    int SourceRevision,
    string Relation,
    string? Quote,
    string? Location,
    string? EventRole,
    string? EventExcerpt);

// ---------- 时间线 / 事件 ----------

public sealed record EventRow(
    string EventId,
    string Role,
    string? Origin,
    string Content,
    string? OccurredAt,
    string? RecordedAt,
    string? CaptureState,
    string? SessionId,
    string? SourceId);

// ---------- 队列 ----------

public sealed record WorkItemRow(
    string WorkId,
    string WorkType,
    string? SubjectRef,
    string State,
    int Attempt,
    string? LastErrorCode,
    string? AvailableAt);

public sealed record WorkErrorRow(
    string Stage,
    string? ErrorCode,
    string? ErrorField,
    string? RecordedAt);

// ---------- 候选 / AI 改动 ----------

public sealed record CandidateRow(
    string CandidateRef,
    int CandidateRevision,
    string ProcessingState,
    string? Reason,
    string? EvidenceFingerprint,
    string? UpdatedAt,
    int EvidenceCount);

public sealed record CandidateEvidenceRow(
    string SourceRef,
    int SourceRevision,
    string? ObservedAt,
    string? EventRole,
    string? EventExcerpt);

// ---------- 审计 ----------

public sealed record DeletionRow(
    string OperationId,
    string Mode,
    int RefCount,
    string? ProjectId,
    string? CreatedAt,
    long? MemoryEpoch,
    string ActiveContentRemoved);

// ---------- 档案 / 查询 ----------

public sealed record ProfileGroupRow(
    string Subject,
    long ClaimCount,
    long ActiveCount,
    string? NewestFrom);

public sealed record TraceStep(
    string Kind,
    string Ref,
    string Relation,
    string? Detail);

// ---------- 共享存储 ----------

public sealed record SharedEntryRow(
    string EntryId,
    string DisplayName,
    string Host,
    string? FirstSeen,
    string? LastSeen);
