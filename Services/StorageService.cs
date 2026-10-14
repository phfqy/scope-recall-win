using Microsoft.Data.Sqlite;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;

namespace ScopeRecallWin.Services;

/// <summary>只读查询通道。实现：Microsoft.Data.Sqlite Mode=ReadOnly + PRAGMA query_only=ON。</summary>
public interface IStorageService
{
    /// <summary>打开只读连接并执行 SQL，返回行集合。</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sqlitePath, string sql, IReadOnlyDictionary<string, object?>? parameters = null);

    // ---------- 领域查询（总览） ----------
    Task<OverviewStats?> GetOverviewStatsAsync(string sqlitePath);

    /// <summary>待确认候选按「状态+卡住原因」的分布（解释「待确认为何没转正」）。</summary>
    Task<IReadOnlyList<CandidateReasonRow>> GetCandidateBreakdownAsync(string sqlitePath);

    // ---------- 领域查询（记忆） ----------
    Task<(IReadOnlyList<ClaimRow> Rows, long Total)> GetClaimsAsync(
        string sqlitePath, string? status, string? query, int page, int pageSize);
    Task<IReadOnlyList<ClaimVersionRow>> GetClaimVersionsAsync(string sqlitePath, string claimId);
    Task<IReadOnlyList<EvidenceRow>> GetClaimEvidenceAsync(string sqlitePath, string claimId);

    // ---------- 领域查询（时间线） ----------
    Task<(IReadOnlyList<EventRow> Rows, long Total)> GetEventsAsync(
        string sqlitePath, string? role, string? dayFrom, string? dayTo, string? query, int limit);

    // ---------- 领域查询（队列） ----------
    Task<(IReadOnlyList<WorkItemRow> Rows, long Total)> GetWorkItemsAsync(
        string sqlitePath, string? state, string? type, int limit);
    Task<IReadOnlyList<WorkErrorRow>> GetWorkErrorsAsync(string sqlitePath, string workId);

    // ---------- 领域查询（候选） ----------
    Task<(IReadOnlyList<CandidateRow> Rows, long Total)> GetCandidatesAsync(
        string sqlitePath, string? state, int limit);
    Task<IReadOnlyList<CandidateEvidenceRow>> GetCandidateEvidenceAsync(string sqlitePath, string candidateRef);

    // ---------- 领域查询（审计） ----------
    Task<IReadOnlyList<DeletionRow>> GetDeletionsAsync(string sqlitePath, int limit);

    // ---------- 领域查询（档案 / 检索） ----------
    Task<IReadOnlyList<ProfileGroupRow>> GetProfileGroupsAsync(string sqlitePath, int limit);
    Task<IReadOnlyList<EventRow>> LexicalSearchAsync(string sqlitePath, string keyword, int limit);
    Task<IReadOnlyList<TraceStep>> TraceAsync(string sqlitePath, string refId);
    Task<IReadOnlyList<ClaimRow>> EntitySearchAsync(string sqlitePath, string entity, int limit);
}

/// <summary>
/// 只读查询实现。
/// 硬性（提示词包约束 2）：连接串必须带 Mode=ReadOnly；连接后 PRAGMA query_only=ON 双保险；
/// 禁止任何写 SQL；绝不 import scope_recall（SQLiteStorage 默认 upgrade_on_open=True 会触发迁移）。
/// 上游口径：claim 证据计数 evidence_links.object_revision 用 '='，episode 用 '&lt;='（core/lineage.py）；
/// source_events 主键 (event_id, source_revision)；时间戳 +00:00 微秒与 Z 两种格式，排序前归一。
/// </summary>
public sealed class StorageService : IStorageService
{
    /// <summary>
    /// 读取失败事件。历史问题：12 个读方法都是 <c>catch { } return rows;</c>——
    /// 库文件损坏/被占/权限不足时静默返回空列表，UI 显示「0 条记录」，
    /// 用户无法区分「没数据」和「读不了」（对一个记忆工具这是致命的误判）。
    /// 现在：既落盘日志（AppLog），也抛本事件让界面显式报错；仍然 fail-soft
    /// 返回空列表，不把异常抛给视图层。
    /// </summary>
    public event EventHandler<StorageReadErrorEventArgs>? ReadFailed;

    /// <summary>读失败统一收口：日志 + 事件。</summary>
    private void OnReadFailed(string method, string sqlitePath, Exception ex)
    {
        AppLog.Warn($"StorageService.{method} 读取失败（{sqlitePath}）：{ex.GetType().Name}: {ex.Message}");
        ReadFailed?.Invoke(this, new StorageReadErrorEventArgs(method, sqlitePath, ex));
    }
    private static SqliteConnection OpenReadOnly(string sqlitePath)
    {
        var connection = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA query_only=ON;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sqlitePath, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using var connection = OpenReadOnly(sqlitePath);
        await using var _ = connection.ConfigureAwait(false);
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
            }
        }
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    // ---------- 总览 ----------

    public async Task<OverviewStats?> GetOverviewStatsAsync(string sqlitePath)
    {
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM claims) AS claim_total,
                  (SELECT COUNT(*) FROM claims c JOIN claim_versions v
                     ON c.claim_id=v.claim_id AND c.current_revision=v.revision
                   WHERE v.state='active') AS claim_active,
                  (SELECT COUNT(*) FROM claims c JOIN claim_versions v
                     ON c.claim_id=v.claim_id AND c.current_revision=v.revision
                   WHERE v.state='proposed') AS claim_proposed,
                  (SELECT COUNT(*) FROM source_events) AS event_total,
                  (SELECT COUNT(*) FROM candidate_lifecycle
                   WHERE processing_state='waiting_evidence') AS candidate_waiting,
                  (SELECT COUNT(*) FROM work_items WHERE state='failed') AS work_failed,
                  (SELECT COUNT(*) FROM work_items WHERE state='pending') AS work_pending,
                  (SELECT COUNT(*) FROM work_items WHERE state='done') AS work_done,
                  (SELECT schema_version FROM instance_meta LIMIT 1) AS schema_version,
                  (SELECT memory_epoch FROM instance_meta LIMIT 1) AS memory_epoch,
                  (SELECT agent_id FROM instance_meta LIMIT 1) AS agent_id,
                  (SELECT data_directory FROM instance_meta LIMIT 1) AS data_directory,
                  (SELECT MAX(occurred_at) FROM source_events) AS newest_event
                """;
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return null;
            }
            double size = 0;
            try { size = new FileInfo(sqlitePath).Length; }
            catch (Exception ex)
            {
                AppLog.Warn($"StorageService: 读取库文件大小失败（{sqlitePath}）：{ex.GetType().Name}: {ex.Message}");
            }
            return new OverviewStats(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                size,
                reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                ParseTs(reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc cref="IStorageService.GetCandidateBreakdownAsync"/>
    public async Task<IReadOnlyList<CandidateReasonRow>> GetCandidateBreakdownAsync(string sqlitePath)
    {
        var rows = new List<CandidateReasonRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            // 只统计当前版本仍为 proposed 的候选 —— 与总览页「待确认」口径一致。
            // 注意：一个 claim 可能有多条 candidate_lifecycle（每个候选版本一行），
            // 直接 JOIN 会重复计数（曾把 455 数成 624），故每条 claim 只取其最新一行生命周期。
            cmd.CommandText = """
                SELECT st, reason, COUNT(*) AS n FROM (
                  SELECT c.claim_id AS cid,
                    (SELECT cl.processing_state FROM candidate_lifecycle cl WHERE cl.candidate_ref = c.claim_id
                       ORDER BY cl.candidate_revision DESC, cl.updated_at DESC LIMIT 1) AS st,
                    (SELECT cl.reason FROM candidate_lifecycle cl WHERE cl.candidate_ref = c.claim_id
                       ORDER BY cl.candidate_revision DESC, cl.updated_at DESC LIMIT 1) AS reason
                  FROM claims c JOIN claim_versions v
                    ON v.claim_id = c.claim_id AND v.revision = c.current_revision
                  WHERE v.state = 'proposed'
                )
                GROUP BY st, reason
                ORDER BY n DESC
                """;
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new CandidateReasonRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.GetInt64(2)));
            }
        }
        catch
        {
            // 读不到就返回空列表：上层据此退回「无明细」的展示
        }
        return rows;
    }

    // ---------- 记忆 ----------

    public async Task<(IReadOnlyList<ClaimRow> Rows, long Total)> GetClaimsAsync(
        string sqlitePath, string? status, string? query, int page, int pageSize)
    {
        var rows = new List<ClaimRow>();
        long total = 0;
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);

            var where = new List<string> { "1=1" };
            if (!string.IsNullOrWhiteSpace(status) && status != "all")
            {
                where.Add("v.state = $status");
            }
            if (!string.IsNullOrWhiteSpace(query))
            {
                where.Add("(c.subject LIKE $q OR c.predicate LIKE $q OR c.claim_id LIKE $q)");
            }

            var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"""
                SELECT COUNT(*)
                FROM claims c JOIN claim_versions v
                  ON c.claim_id=v.claim_id AND c.current_revision=v.revision
                WHERE {string.Join(" AND ", where)}
                """;
            var listCmd = connection.CreateCommand();
            listCmd.CommandText = $"""
                SELECT c.claim_id, c.subject, c.predicate, c.kind, v.state,
                       c.current_revision,
                       (SELECT COUNT(*) FROM claim_versions cv WHERE cv.claim_id=c.claim_id) AS version_count,
                       (SELECT COUNT(*) FROM evidence_links e
                         WHERE e.object_kind='claim' AND e.object_ref=c.claim_id
                           AND e.object_revision=c.current_revision) AS evidence_count,
                       c.suppressed
                FROM claims c JOIN claim_versions v
                  ON c.claim_id=v.claim_id AND c.current_revision=v.revision
                WHERE {string.Join(" AND ", where)}
                ORDER BY c.claim_id
                LIMIT $limit OFFSET $offset
                """;
            if (!string.IsNullOrWhiteSpace(status) && status != "all")
            {
                var p1 = countCmd.CreateParameter(); p1.ParameterName = "$status"; p1.Value = status;
                countCmd.Parameters.Add(p1);
                var p2 = listCmd.CreateParameter(); p2.ParameterName = "$status"; p2.Value = status;
                listCmd.Parameters.Add(p2);
            }
            if (!string.IsNullOrWhiteSpace(query))
            {
                var like = $"%{query}%";
                var q1 = countCmd.CreateParameter(); q1.ParameterName = "$q"; q1.Value = like;
                countCmd.Parameters.Add(q1);
                var q2 = listCmd.CreateParameter(); q2.ParameterName = "$q"; q2.Value = like;
                listCmd.Parameters.Add(q2);
            }
            var l1 = countCmd.CreateParameter(); l1.ParameterName = "$limit"; l1.Value = pageSize;
            countCmd.Parameters.Add(l1);
            var l2 = listCmd.CreateParameter(); l2.ParameterName = "$limit"; l2.Value = pageSize;
            listCmd.Parameters.Add(l2);
            var o1 = countCmd.CreateParameter(); o1.ParameterName = "$offset"; o1.Value = (page - 1) * pageSize;
            countCmd.Parameters.Add(o1);
            var o2 = listCmd.CreateParameter(); o2.ParameterName = "$offset"; o2.Value = (page - 1) * pageSize;
            listCmd.Parameters.Add(o2);

            total = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false));
            await using var reader = await listCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new ClaimRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt32(5),
                    reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    !reader.IsDBNull(8) && reader.GetInt64(8) != 0));
            }
        }
        catch
        {
            // 查询失败返回空（视图展示错误态）
        }
        return (rows, total);
    }

    public async Task<IReadOnlyList<ClaimVersionRow>> GetClaimVersionsAsync(string sqlitePath, string claimId)
    {
        var rows = new List<ClaimVersionRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT revision, state, basis, qualification_reason, valid_from, valid_to,
                       recorded_from, payload_json, replaces_revision
                FROM claim_versions WHERE claim_id=$id ORDER BY revision DESC
                """;
            cmd.Parameters.AddWithValue("$id", claimId);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new ClaimVersionRow(
                    reader.GetInt32(0), reader.GetString(1), S(reader, 2), S(reader, 3),
                    S(reader, 4), S(reader, 5), S(reader, 6), reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8).ToString()));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetClaimVersionsAsync), sqlitePath, ex);
        }
        return rows;
    }

    public async Task<IReadOnlyList<EvidenceRow>> GetClaimEvidenceAsync(string sqlitePath, string claimId)
    {
        var rows = new List<EvidenceRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT e.source_ref, e.source_revision, e.relation, e.quote, e.location,
                       s.role, substr(s.content,1,120)
                FROM evidence_links e
                LEFT JOIN source_events s ON s.event_id=e.source_ref
                WHERE e.object_kind='claim' AND e.object_ref=$id
                ORDER BY e.source_revision
                """;
            cmd.Parameters.AddWithValue("$id", claimId);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new EvidenceRow(
                    reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                    S(reader, 3), S(reader, 4), S(reader, 5), S(reader, 6)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetClaimEvidenceAsync), sqlitePath, ex);
        }
        return rows;
    }

    // ---------- 时间线 ----------

    public async Task<(IReadOnlyList<EventRow> Rows, long Total)> GetEventsAsync(
        string sqlitePath, string? role, string? dayFrom, string? dayTo, string? query, int limit)
    {
        var rows = new List<EventRow>();
        long total = 0;
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);

            var where = new List<string> { "1=1" };
            if (!string.IsNullOrWhiteSpace(role) && role != "all")
            {
                where.Add("role = $role");
            }
            if (!string.IsNullOrWhiteSpace(dayFrom))
            {
                where.Add("occurred_at >= $from");
            }
            if (!string.IsNullOrWhiteSpace(dayTo))
            {
                where.Add("occurred_at <= $to");
            }
            if (!string.IsNullOrWhiteSpace(query))
            {
                where.Add("content LIKE $q");
            }

            var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM source_events WHERE {string.Join(" AND ", where)}";
            var listCmd = connection.CreateCommand();
            listCmd.CommandText = $"""
                SELECT event_id, role, origin, content, occurred_at, recorded_at,
                       capture_state, session_id, source_id
                FROM source_events
                WHERE {string.Join(" AND ", where)}
                ORDER BY occurred_at DESC
                LIMIT $limit
                """;
            void AddParam(string name, object value)
            {
                var a = countCmd.CreateParameter(); a.ParameterName = name; a.Value = value;
                countCmd.Parameters.Add(a);
                var b = listCmd.CreateParameter(); b.ParameterName = name; b.Value = value;
                listCmd.Parameters.Add(b);
            }
            if (!string.IsNullOrWhiteSpace(role) && role != "all") { AddParam("$role", role); }
            if (!string.IsNullOrWhiteSpace(dayFrom)) { AddParam("$from", dayFrom); }
            if (!string.IsNullOrWhiteSpace(dayTo)) { AddParam("$to", dayTo); }
            if (!string.IsNullOrWhiteSpace(query)) { AddParam("$q", $"%{query}%"); }
            var la = listCmd.CreateParameter(); la.ParameterName = "$limit"; la.Value = limit;
            listCmd.Parameters.Add(la);

            total = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false));
            await using var reader = await listCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new EventRow(
                    reader.GetString(0), reader.GetString(1), S(reader, 2), reader.GetString(3),
                    S(reader, 4), S(reader, 5), S(reader, 6), S(reader, 7), S(reader, 8)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetEventsAsync), sqlitePath, ex);
        }
        return (rows, total);
    }

    // ---------- 队列 ----------

    public async Task<(IReadOnlyList<WorkItemRow> Rows, long Total)> GetWorkItemsAsync(
        string sqlitePath, string? state, string? type, int limit)
    {
        var rows = new List<WorkItemRow>();
        long total = 0;
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var where = new List<string> { "1=1" };
            if (!string.IsNullOrWhiteSpace(state) && state != "all") { where.Add("state = $state"); }
            if (!string.IsNullOrWhiteSpace(type) && type != "all") { where.Add("work_type = $type"); }
            var clause = string.Join(" AND ", where);

            var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM work_items WHERE {clause}";
            var listCmd = connection.CreateCommand();
            listCmd.CommandText = $"""
                SELECT work_id, work_type, subject_ref, state, attempt, last_error_code, available_at
                FROM work_items WHERE {clause}
                ORDER BY CASE state WHEN 'failed' THEN 0 WHEN 'pending' THEN 1 ELSE 2 END,
                         available_at DESC
                LIMIT $limit
                """;
            void AddParam(string name, object value)
            {
                var a = countCmd.CreateParameter(); a.ParameterName = name; a.Value = value;
                countCmd.Parameters.Add(a);
                var b = listCmd.CreateParameter(); b.ParameterName = name; b.Value = value;
                listCmd.Parameters.Add(b);
            }
            if (!string.IsNullOrWhiteSpace(state) && state != "all") { AddParam("$state", state); }
            if (!string.IsNullOrWhiteSpace(type) && type != "all") { AddParam("$type", type); }
            var la = listCmd.CreateParameter(); la.ParameterName = "$limit"; la.Value = limit;
            listCmd.Parameters.Add(la);

            total = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false));
            await using var reader = await listCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new WorkItemRow(
                    reader.GetString(0), reader.GetString(1), S(reader, 2), reader.GetString(3),
                    reader.GetInt32(4), S(reader, 5), S(reader, 6)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetWorkItemsAsync), sqlitePath, ex);
        }
        return (rows, total);
    }

    public async Task<IReadOnlyList<WorkErrorRow>> GetWorkErrorsAsync(string sqlitePath, string workId)
    {
        var rows = new List<WorkErrorRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT stage, error_code, error_field, recorded_at
                FROM work_error_details WHERE work_id=$id ORDER BY recorded_at DESC LIMIT 20
                """;
            cmd.Parameters.AddWithValue("$id", workId);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new WorkErrorRow(S(reader, 0) ?? "", S(reader, 1), S(reader, 2), S(reader, 3)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetWorkErrorsAsync), sqlitePath, ex);
        }
        return rows;
    }

    // ---------- 候选 ----------

    public async Task<(IReadOnlyList<CandidateRow> Rows, long Total)> GetCandidatesAsync(
        string sqlitePath, string? state, int limit)
    {
        var rows = new List<CandidateRow>();
        long total = 0;
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var where = "1=1";
            if (!string.IsNullOrWhiteSpace(state) && state != "all") { where = "processing_state = $state"; }

            var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM candidate_lifecycle WHERE {where}";
            var listCmd = connection.CreateCommand();
            listCmd.CommandText = $"""
                SELECT c.candidate_ref, c.candidate_revision, c.processing_state, c.reason,
                       c.evidence_fingerprint, c.updated_at,
                       (SELECT COUNT(*) FROM candidate_evidence e
                         WHERE e.candidate_ref=c.candidate_ref) AS evidence_count
                FROM candidate_lifecycle c
                WHERE {where}
                ORDER BY c.updated_at DESC
                LIMIT $limit
                """;
            if (!string.IsNullOrWhiteSpace(state) && state != "all")
            {
                var a = countCmd.CreateParameter(); a.ParameterName = "$state"; a.Value = state;
                countCmd.Parameters.Add(a);
                var b = listCmd.CreateParameter(); b.ParameterName = "$state"; b.Value = state;
                listCmd.Parameters.Add(b);
            }
            var la = listCmd.CreateParameter(); la.ParameterName = "$limit"; la.Value = limit;
            listCmd.Parameters.Add(la);

            total = Convert.ToInt64(await countCmd.ExecuteScalarAsync().ConfigureAwait(false));
            await using var reader = await listCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new CandidateRow(
                    reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                    S(reader, 3), S(reader, 4), S(reader, 5), reader.GetInt32(6)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetCandidatesAsync), sqlitePath, ex);
        }
        return (rows, total);
    }

    public async Task<IReadOnlyList<CandidateEvidenceRow>> GetCandidateEvidenceAsync(
        string sqlitePath, string candidateRef)
    {
        var rows = new List<CandidateEvidenceRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT e.source_ref, e.source_revision, e.observed_at, s.role, substr(s.content,1,120)
                FROM candidate_evidence e
                LEFT JOIN source_events s ON s.event_id=e.source_ref
                WHERE e.candidate_ref=$id
                ORDER BY e.observed_at DESC LIMIT 50
                """;
            cmd.Parameters.AddWithValue("$id", candidateRef);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new CandidateEvidenceRow(
                    reader.GetString(0), reader.GetInt32(1), S(reader, 2), S(reader, 3), S(reader, 4)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetCandidateEvidenceAsync), sqlitePath, ex);
        }
        return rows;
    }

    // ---------- 审计 ----------

    public async Task<IReadOnlyList<DeletionRow>> GetDeletionsAsync(string sqlitePath, int limit)
    {
        var rows = new List<DeletionRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT d.operation_id, d.mode,
                       (SELECT COUNT(*) FROM deletion_members m WHERE m.operation_id=d.operation_id) AS ref_count,
                       d.project_id, d.created_at, d.memory_epoch, d.active_content_removed
                FROM deletion_operations d
                ORDER BY d.created_at DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new DeletionRow(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    S(reader, 3), S(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    S(reader, 6)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetDeletionsAsync), sqlitePath, ex);
        }
        return rows;
    }

    // ---------- 档案 / 检索 ----------

    public async Task<IReadOnlyList<ProfileGroupRow>> GetProfileGroupsAsync(string sqlitePath, int limit)
    {
        var rows = new List<ProfileGroupRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT c.subject,
                       COUNT(*) AS claim_count,
                       SUM(CASE WHEN v.state='active' THEN 1 ELSE 0 END) AS active_count,
                       MAX(v.valid_from) AS newest_from
                FROM claims c JOIN claim_versions v
                  ON c.claim_id=v.claim_id AND c.current_revision=v.revision
                GROUP BY c.subject
                ORDER BY claim_count DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new ProfileGroupRow(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), S(reader, 3)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(GetProfileGroupsAsync), sqlitePath, ex);
        }
        return rows;
    }

    public async Task<IReadOnlyList<EventRow>> LexicalSearchAsync(string sqlitePath, string keyword, int limit)
    {
        var rows = new List<EventRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT s.event_id, s.role, s.origin, s.content, s.occurred_at, s.recorded_at,
                       s.capture_state, s.session_id, s.source_id
                FROM lexical_postings p
                JOIN lexical_terms t ON t.term_id = p.term_id
                JOIN source_events s ON s.event_id = p.source_id
                WHERE t.term LIKE $kw
                GROUP BY s.event_id
                ORDER BY s.occurred_at DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$kw", $"%{keyword}%");
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new EventRow(
                    reader.GetString(0), reader.GetString(1), S(reader, 2), reader.GetString(3),
                    S(reader, 4), S(reader, 5), S(reader, 6), S(reader, 7), S(reader, 8)));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(LexicalSearchAsync), sqlitePath, ex);
        }
        return rows;
    }

    public async Task<IReadOnlyList<TraceStep>> TraceAsync(string sqlitePath, string refId)
    {
        var rows = new List<TraceStep>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);

            // claim 反查：作为对象的证据（supports）
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT 'claim ← evidence' AS kind, e.source_ref AS ref, e.relation,
                       substr(s.content,1,100) AS detail
                FROM evidence_links e
                LEFT JOIN source_events s ON s.event_id=e.source_ref
                WHERE e.object_kind='claim' AND e.object_ref=$id
                ORDER BY e.source_revision
                """;
            cmd.Parameters.AddWithValue("$id", refId);
            await using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    rows.Add(new TraceStep(reader.GetString(0), reader.GetString(1), reader.GetString(2), S(reader, 3)));
                }
            }

            // event 反查：它支持的 claim 与派生 episode
            var cmd2 = connection.CreateCommand();
            cmd2.CommandText = """
                SELECT 'event → claim' AS kind, e.object_ref AS ref, e.relation,
                       (SELECT subject FROM claims c WHERE c.claim_id=e.object_ref) AS detail
                FROM evidence_links e
                WHERE e.object_kind='claim' AND e.source_ref=$id
                UNION ALL
                SELECT 'event → episode' AS kind, e.object_ref, e.relation, NULL
                FROM evidence_links e
                WHERE e.object_kind='episode' AND e.source_ref=$id
                """;
            cmd2.Parameters.AddWithValue("$id", refId);
            await using (var reader2 = await cmd2.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader2.ReadAsync().ConfigureAwait(false))
                {
                    rows.Add(new TraceStep(reader2.GetString(0), reader2.GetString(1), reader2.GetString(2), S(reader2, 3)));
                }
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(TraceAsync), sqlitePath, ex);
        }
        return rows;
    }

    public async Task<IReadOnlyList<ClaimRow>> EntitySearchAsync(string sqlitePath, string entity, int limit)
    {
        var rows = new List<ClaimRow>();
        try
        {
            using var connection = OpenReadOnly(sqlitePath);
            await using var _ = connection.ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT c.claim_id, c.subject, c.predicate, c.kind, v.state, c.current_revision,
                       0, 0, c.suppressed
                FROM claims c JOIN claim_versions v
                  ON c.claim_id=v.claim_id AND c.current_revision=v.revision
                WHERE c.subject = $entity OR c.subject LIKE $like
                ORDER BY c.claim_id LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$entity", entity);
            cmd.Parameters.AddWithValue("$like", $"%{entity}%");
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new ClaimRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt32(5), 0, 0,
                    !reader.IsDBNull(8) && reader.GetInt64(8) != 0));
            }
        }
        catch (Exception ex)
        {
            OnReadFailed(nameof(EntitySearchAsync), sqlitePath, ex);
        }
        return rows;
    }

    // ---------- 工具 ----------

    private static string? S(SqliteDataReader reader, int i)
        => reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();

    /// <summary>时间戳归一（+00:00 微秒与 Z 两种格式）→ DateTimeOffset。</summary>
    internal static DateTimeOffset? ParseTs(string? text)
        => Helpers.TimestampNormalizer.Parse(text);
}

/// <summary>读取失败详情（方法名 / 库路径 / 异常），供 <see cref="StorageService.ReadFailed"/> 与测试断言。</summary>
public sealed record StorageReadErrorEventArgs(string Method, string SqlitePath, Exception Exception);
