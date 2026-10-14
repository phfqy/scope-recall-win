using Xunit;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Tests;

/// <summary>
/// 存储层**错误路径**回归（评审 P0：12 处 <c>catch { } return rows;</c> 曾把
/// 「读不了」静默冒充成「0 条记录」——静态检查拦不住这类问题，测试可以）。
/// </summary>
public sealed class StorageServiceErrorPathTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevLogDir;

    public StorageServiceErrorPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sr-tests-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_tempDir);
        // 日志重定向到临时目录：不污染真实日志，也便于断言内容
        _prevLogDir = Environment.GetEnvironmentVariable("SR_LOG_DIR");
        Environment.SetEnvironmentVariable("SR_LOG_DIR", Path.Combine(_tempDir, "logs"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SR_LOG_DIR", _prevLogDir);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private static (StorageService Service, List<StorageReadErrorEventArgs> Captured) NewService()
    {
        var svc = new StorageService();
        var captured = new List<StorageReadErrorEventArgs>();
        svc.ReadFailed += (_, e) => captured.Add(e);
        return (svc, captured);
    }

    [Fact]
    public async Task CorruptDb_ReadFails_DoesNotThrow_AndRaisesEvent()
    {
        var (svc, captured) = NewService();
        var badDb = Path.Combine(_tempDir, "corrupt.sqlite3");
        await File.WriteAllTextAsync(badDb, "这不是 SQLite 文件，是纯文本。");

        var rows = await svc.GetEventsAsync(badDb, null, null, null, null, 50);

        // fail-soft：不把异常抛给视图层
        Assert.Empty(rows.Rows);
        // 但必须显式告知失败（而不是静默空列表）
        Assert.Single(captured);
        Assert.Equal(nameof(StorageService.GetEventsAsync), captured[0].Method);
        Assert.Equal(badDb, captured[0].SqlitePath);
        Assert.NotNull(captured[0].Exception);
    }

    [Fact]
    public async Task MissingDb_ReadFails_RaisesEvent()
    {
        var (svc, captured) = NewService();
        var missing = Path.Combine(_tempDir, "no-such-file.sqlite3");

        var rows = await svc.GetEventsAsync(missing, null, null, null, null, 50);

        Assert.Empty(rows.Rows);
        Assert.Single(captured);
    }

    [Fact]
    public async Task LockedOrUnreadableDb_AllReadMethods_RaiseEvent()
    {
        // 逐个读方法打一遍：确保 12 处 catch 无一漏网（漏一处就静默一次）
        var badDb = Path.Combine(_tempDir, "corrupt-all.sqlite3");
        await File.WriteAllTextAsync(badDb, "garbage-not-sqlite");

        var svc = new StorageService();
        var count = 0;
        svc.ReadFailed += (_, _) => count++;

        await svc.GetClaimVersionsAsync(badDb, "claim-1");
        await svc.GetClaimEvidenceAsync(badDb, "claim-1");
        await svc.GetEventsAsync(badDb, null, null, null, null, 50);
        await svc.GetWorkItemsAsync(badDb, null, null, 50);
        await svc.GetWorkErrorsAsync(badDb, "work-1");
        await svc.GetCandidatesAsync(badDb, null, 50);
        await svc.GetCandidateEvidenceAsync(badDb, "cand-1");
        await svc.GetDeletionsAsync(badDb, 50);
        await svc.GetProfileGroupsAsync(badDb, 50);
        await svc.LexicalSearchAsync(badDb, "关键词", 50);
        await svc.TraceAsync(badDb, "ref-1");
        await svc.EntitySearchAsync(badDb, "实体", 50);

        Assert.Equal(12, count);
    }

    [Fact]
    public async Task ReadFailure_IsWrittenToLogFile()
    {
        var svc = new StorageService();
        var badDb = Path.Combine(_tempDir, "corrupt-log.sqlite3");
        await File.WriteAllTextAsync(badDb, "garbage");

        await svc.GetEventsAsync(badDb, null, null, null, null, 50);

        var logFile = Assert.Single(Directory.GetFiles(AppLog.LogDir, "app-*.log"));
        var text = await File.ReadAllTextAsync(logFile);
        Assert.Contains(nameof(StorageService.GetEventsAsync), text, StringComparison.Ordinal);
        Assert.Contains("WARN", text, StringComparison.Ordinal);
        Assert.Contains(badDb, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthyDb_ParameterizedQuery_ReturnsRows_NoEvent()
    {
        var db = Path.Combine(_tempDir, "ok.sqlite3");
        await using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(a TEXT, b INTEGER); INSERT INTO t VALUES('x', 1), ('y', 2);";
            await cmd.ExecuteNonQueryAsync();
        }

        var (svc, captured) = NewService();
        var rows = await svc.QueryAsync(db, "SELECT a, b FROM t WHERE b >= @min ORDER BY a",
            new Dictionary<string, object?> { ["min"] = 2 });

        Assert.Single(rows);
        Assert.Empty(captured);
    }

    [Fact]
    public async Task ReadOnlyConnection_RejectsWrites()
    {
        // 双保险：Mode=ReadOnly + PRAGMA query_only=ON，写必须被拒
        var db = Path.Combine(_tempDir, "ro.sqlite3");
        await using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(a TEXT);";
            await cmd.ExecuteNonQueryAsync();
        }

        var svc = new StorageService();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => svc.QueryAsync(db, "INSERT INTO t VALUES('nope')"));
    }
}

/// <summary>AppLog 自身的行为回归（日志设施不能成为新的故障点）。</summary>
public sealed class AppLogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevLogDir;
    private readonly AppLogLevel _prevLevel;

    public AppLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sr-logtest-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_tempDir);
        _prevLogDir = Environment.GetEnvironmentVariable("SR_LOG_DIR");
        _prevLevel = AppLog.Level;
        Environment.SetEnvironmentVariable("SR_LOG_DIR", Path.Combine(_tempDir, "logs"));
        AppLog.Level = AppLogLevel.Debug;
    }

    public void Dispose()
    {
        AppLog.Level = _prevLevel;
        Environment.SetEnvironmentVariable("SR_LOG_DIR", _prevLogDir);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void WritesLines_WithLevelAndMessage()
    {
        AppLog.Warn("测试消息A", new InvalidOperationException("测试异常"));

        var logFile = Assert.Single(Directory.GetFiles(AppLog.LogDir, "app-*.log"));
        var text = File.ReadAllText(logFile);
        Assert.Contains("测试消息A", text, StringComparison.Ordinal);
        Assert.Contains("[WARN", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LevelFilter_BelowCurrentLevel_IsNotWritten()
    {
        AppLog.Level = AppLogLevel.Warn;
        AppLog.Debug("调试信息不该出现");
        AppLog.Warn("警告信息该出现");

        var logFile = Assert.Single(Directory.GetFiles(AppLog.LogDir, "app-*.log"));
        var text = File.ReadAllText(logFile);
        Assert.DoesNotContain("调试信息不该出现", text, StringComparison.Ordinal);
        Assert.Contains("警告信息该出现", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnwritablePath_NeverThrows()
    {
        // 日志写不进去时只能吞掉——记录行为本身不能再制造新的静默失败（更别说抛异常）
        Environment.SetEnvironmentVariable("SR_LOG_DIR", @"Z:\definitely\no\such\dir\__");
        AppLog.Info("往不存在的盘写");
        AppLog.Warn("往不存在的盘写", new Exception("x"));
        AppLog.Error("来源", new Exception("y"));
    }

    [Fact]
    public void LogDir_HonoursOverride()
    {
        Environment.SetEnvironmentVariable("SR_LOG_DIR", _tempDir);
        Assert.Equal(_tempDir, AppLog.LogDir);
    }
}
