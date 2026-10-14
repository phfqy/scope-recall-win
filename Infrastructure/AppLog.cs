namespace ScopeRecallWin.Infrastructure;

/// <summary>日志级别（默认 Info；可用环境变量 SR_LOG_LEVEL=debug|info|warn|error 调整）。</summary>
public enum AppLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 始终落盘的文件日志。
///
/// 为什么需要它：代码评审发现全项目此前只有 <see cref="App.Trace"/>（SR_TRACE=1 门控的
/// 启动跟踪，默认关闭）和 <see cref="App.LogCrash"/>（仅未处理异常）。运行期的**已捕获**
/// 异常——尤其是 StorageService 里 12 处 <c>catch { }</c> 静默返回空列表的读失败——
/// 没有任何痕迹，用户看到「0 条记录」而无从判断是没数据还是读不了。
///
/// 设计约束：
/// - 按天滚动（app-YYYYMMDD.log），保留 14 天，单文件超 4MB 重开；
/// - 自身任何 IO 失败都吞掉——日志永不影响主流程（这与它要解决的问题同源：
///   「记录行为本身不能再制造新的静默失败」）；
/// - SR_LOG_DIR 可重定向目录（测试用）；SR_LOG_LEVEL 可调级别。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static AppLogLevel _level = ParseLevel(Environment.GetEnvironmentVariable("SR_LOG_LEVEL"));
    private static bool _pruned;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private const long MaxFileBytes = 4L << 20;

    /// <summary>日志目录：默认 %LOCALAPPDATA%\ScopeRecallWin\logs；SR_LOG_DIR 可覆盖。</summary>
    public static string LogDir =>
        Environment.GetEnvironmentVariable("SR_LOG_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : System.IO.Path.Combine(AppSettings.SettingsDir, "logs");

    /// <summary>当前级别（运行期可改，便于设置页将来接进去）。</summary>
    public static AppLogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    public static void Debug(string message) => Write(AppLogLevel.Debug, message, null);

    public static void Info(string message) => Write(AppLogLevel.Info, message, null);

    public static void Warn(string message, Exception? ex = null) => Write(AppLogLevel.Warn, message, ex);

    public static void Error(string source, Exception ex) => Write(AppLogLevel.Error, source, ex);

    private static void Write(AppLogLevel level, string message, Exception? ex)
    {
        try
        {
            if (level < _level)
            {
                return;
            }
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {message}";
            if (ex is not null)
            {
                line += $" | {ex.GetType().Name}: {ex.Message}";
            }
            lock (Gate)
            {
                var dir = LogDir;
                System.IO.Directory.CreateDirectory(dir);
                PruneOldFiles(dir);
                var path = System.IO.Path.Combine(dir, $"app-{DateTimeOffset.Now:yyyyMMdd}.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    File.Delete(path); // 单日超限：重开优于无限增长
                }
                File.AppendAllText(path, line + "\n");
            }
            App.Trace(line); // SR_TRACE=1 时同步进启动跟踪，便于一次看全
        }
        catch
        {
            // 日志不可写时忽略：记录行为本身绝不能成为新的故障点
        }
    }

    /// <summary>删除超过保留期的日志（每进程只做一次）。</summary>
    private static void PruneOldFiles(string dir)
    {
        if (_pruned)
        {
            return;
        }
        _pruned = true;
        try
        {
            var cutoff = DateTimeOffset.Now - Retention;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("app-*.log"))
            {
                if (f.LastWriteTime < cutoff)
                {
                    f.Delete();
                }
            }
        }
        catch
        {
            // 清理失败不影响本次写入
        }
    }

    private static AppLogLevel ParseLevel(string? raw) => raw?.ToLowerInvariant() switch
    {
        "debug" => AppLogLevel.Debug,
        "warn" => AppLogLevel.Warn,
        "error" => AppLogLevel.Error,
        _ => AppLogLevel.Info,
    };
}
