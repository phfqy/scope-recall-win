namespace ScopeRecallWin.Services;

/// <summary>只读查询通道。P2 实现：Microsoft.Data.Sqlite Mode=ReadOnly，绝不 import scope_recall。</summary>
public interface IStorageService
{
    /// <summary>打开只读连接并执行 SQL，返回行集合。</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sqlitePath, string sql, IReadOnlyDictionary<string, object?>? parameters = null);
}

/// <summary>
/// 只读查询实现骨架。
/// 硬性（提示词包约束 2）：连接串必须带 Mode=ReadOnly；禁止任何写 SQL；绝不 import scope_recall
/// （SQLiteStorage 默认 upgrade_on_open=True 会触发迁移）。
/// 上游口径（P2 落实）：
///   claim 证据计数 evidence_links.object_revision 用 '='，episode 用 '&lt;='（core/lineage.py）；
///   source_events 主键 (event_id, source_revision)；时间戳 +00:00 微秒与 Z 两种格式，归一后排序。
/// </summary>
public sealed class StorageService : IStorageService
{
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sqlitePath, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        // P2 实现：Microsoft.Data.Sqlite + ConnectionString Mode=ReadOnly（等价三重只读）。
        // 本骨架仅保留接口与口径注释，避免在未验证的平台上产生编译期依赖；P2 时替换为真实实现。
        await Task.CompletedTask;
        return Array.Empty<IReadOnlyDictionary<string, object?>>();
    }
}
