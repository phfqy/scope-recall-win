using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScopeRecallWin.Infrastructure;

namespace ScopeRecallWin.Services;

/// <summary>云同步一轮的结果（对应 Python 侧 status.json）。</summary>
public sealed record CloudSyncRound(
    string? Status,
    int Pushed,
    int Pulled,
    int WrittenToStore,
    int Unchanged,
    int Conflicts,
    int SkippedDuplicates,
    double DurationSeconds,
    string? RoundStarted,
    string? WrittenAt,
    IReadOnlyList<string> Errors,
    int ClaimsActive,
    int SourceEvents)
{
    public bool Ok => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
    public bool Partial => string.Equals(Status, "partial", StringComparison.OrdinalIgnoreCase);
}

/// <summary>云同步配置视图（口令不在此结构里 —— 它只走凭据管理器）。</summary>
public sealed record CloudSyncConfig(
    bool Enabled,
    string Backend,
    string? Url,
    string? User,
    string? Bucket,
    string? Endpoint,
    string? AccessKeyId,
    string Region,
    string Root,
    int IntervalSeconds,
    bool IncludeEvents,
    string? PythonPath)
{
    public static CloudSyncConfig From(AppSettings s) => new(
        s.CloudSyncEnabled, s.CloudSyncBackend, s.CloudSyncUrl, s.CloudSyncUser,
        s.CloudSyncBucket, s.CloudSyncEndpoint, s.CloudSyncAccessKeyId,
        s.CloudSyncRegion, s.CloudSyncRoot,
        s.CloudSyncIntervalSeconds, s.CloudSyncIncludeEvents, s.CloudSyncPythonPath);
}

/// <summary>
/// 记忆云同步（本机共享库 ↔ 云端 ↔ 远端助手）。
/// </summary>
/// <remarks>
/// 架构：控制台**托管**一个独立的 Python 子进程（bridge/cloudsync_run.py）跑一轮同步。
/// 它自己**从不打开或写记忆库**，所有记忆库读写都发生在子进程里：
/// <list type="bullet">
/// <item><b>读</b>：真实共享库以 <c>mode=ro</c> + <c>query_only=1</c> 打开，逐条增量提取；</item>
/// <item><b>写</b>：只经官方 <c>propose_memory</c>（cloud entry 身份），控制台不写一行 SQL。</item>
/// </list>
/// 因此控制台原有的「写只经官方 CLI」边界仍然成立 —— 云同步是**第二个写入口，但同样走官方通道**，
/// 且必须经用户显式授权（<see cref="RunOnceAsync"/> 的授权句 + 先备份）。
///
/// 口令（WebDAV 应用密码 / S3 Secret）只从 Windows 凭据管理器读取，注入子进程环境变量，
/// 绝不出现在命令行参数（进程列表可见）、settings.json 或日志里。
/// </remarks>
public interface ICloudSyncService
{
    /// <summary>远端 agent 链接状态（读云端账本聚合 origin_agent；只读，不碰共享库）。</summary>
    Task<RemoteLinkStatus?> GetRemoteLinkStatusAsync(string memoryRoot, CancellationToken ct = default);
    /// <summary>读取当前配置。</summary>
    CloudSyncConfig GetConfig();

    /// <summary>保存非敏感配置。</summary>
    void SaveConfig(CloudSyncConfig config);

    /// <summary>解析桥脚本路径（随应用一起分发）。</summary>
    string? ResolveBridgeScript();

    /// <summary>解析运行桥的 Python 解释器（专用 venv，优先于插件 venv）。</summary>
    string? ResolvePython(string memoryRoot);

    /// <summary>环境自检：Python 可用性、依赖、桥脚本、云端连通性。</summary>
    Task<CloudSyncPreflight> PreflightAsync(string memoryRoot, CancellationToken ct = default);

    /// <summary>跑一轮同步。写操作前必须已有备份（<paramref name="backupPath"/> 非空）。</summary>
    Task<CloudSyncRound> RunOnceAsync(string memoryRoot, string? backupPath, CancellationToken ct = default);

    /// <summary>读取最近一轮的状态（status.json）。</summary>
    CloudSyncRound? ReadLastStatus(string memoryRoot);

    /// <summary>
    /// 确保云桥成员（entry=cloud）已接入共享库。写入通道的前提：
    /// propose_memory 需要该成员的身份与捕获范围，未接入时写入会被拒。
    /// </summary>
    Task<CloudEntryResult> EnsureCloudEntryAsync(string memoryRoot, CancellationToken ct = default);
}

/// <summary>云桥成员接入结果。</summary>
public sealed record CloudEntryResult(bool Attached, bool AlreadyPresent, string Message, string? RawOutput);

/// <summary>云端账本里识别到的一个远端写入者（按 origin_agent 聚合；home 主桥的记录无该字段）。</summary>
public sealed record RemoteLinkWriter(
    string Agent, int Records, int Claims, int Events, double LastUpdateTs, string? LastUpdateIso);

/// <summary>
/// 远端 agent 链接状态：读云端账本（state.json + 最近记录抽样）聚合 origin_agent 得出。
/// 与远端 CLI sr_cloud_remote.py（SKILL sr-cloud-memory）共用同一账本 —— 远端 propose
/// 打 origin_agent 标记，控制台据此显示「谁在连、最近何时、贡献了多少」。
/// </summary>
public sealed record RemoteLinkStatus(
    bool Reachable,
    string? Error,
    int TotalRecords,
    int Claims,
    int Events,
    bool Sampled,
    int SampleSize,
    string? LastUpdateIso,
    IReadOnlyList<RemoteLinkWriter> Writers,
    CloudSyncRound? LastRound,
    bool Fallback = false)
{
    /// <summary>最近一次云端更新距现在多久（人话；账本为空返回 null）。</summary>
    public string? LastUpdateHuman
    {
        get
        {
            if (LastUpdateIso is null)
            {
                return null;
            }
            if (!DateTimeOffset.TryParse(LastUpdateIso, out var ts))
            {
                return LastUpdateIso;
            }
            var span = DateTimeOffset.Now - ts;
            return span.TotalMinutes < 1 ? "刚刚"
                : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} 分钟前"
                : span.TotalDays < 1 ? $"{(int)span.TotalHours} 小时前"
                : $"{(int)span.TotalDays} 天前";
        }
    }
}

/// <summary>环境自检结果。</summary>
public sealed record CloudSyncPreflight(
    bool PythonFound,
    string? PythonPath,
    string? PythonVersion,
    bool BridgeScriptFound,
    string? BridgeScript,
    bool DependenciesOk,
    IReadOnlyList<string> MissingModules,
    string? Message)
{
    public bool Ready => PythonFound && BridgeScriptFound && DependenciesOk;
}

public sealed partial class CloudSyncService : ICloudSyncService
{
    /// <summary>凭据管理器里的服务名（Target 形如 ScopeRecall:cloudsync:webdav）。</summary>
    public const string CredentialServiceName = "cloudsync";

    /// <summary>桥脚本相对应用目录的候选位置。</summary>
    private static readonly string[] BridgeRelativeCandidates =
    {
        Path.Combine("bridge", "cloudsync_run.py"),
        Path.Combine("cloudsync", "cloudsync_run.py"),
        "cloudsync_run.py",
    };

    private readonly ICredentialService? _credentials;

    public CloudSyncService(ICredentialService? credentials = null) => _credentials = credentials;

    public CloudSyncConfig GetConfig() => CloudSyncConfig.From(App.Settings);

    public void SaveConfig(CloudSyncConfig config)
    {
        var s = App.Settings;
        s.CloudSyncEnabled = config.Enabled;
        s.CloudSyncBackend = config.Backend;
        s.CloudSyncUrl = config.Url;
        s.CloudSyncUser = config.User;
        s.CloudSyncBucket = config.Bucket;
        s.CloudSyncEndpoint = config.Endpoint;
        s.CloudSyncAccessKeyId = config.AccessKeyId;
        s.CloudSyncRegion = config.Region;
        s.CloudSyncRoot = config.Root;
        s.CloudSyncIntervalSeconds = config.IntervalSeconds;
        s.CloudSyncIncludeEvents = config.IncludeEvents;
        s.CloudSyncPythonPath = config.PythonPath;
        s.Save();
    }

    public string? ResolveBridgeScript()
    {
        foreach (var relative in BridgeRelativeCandidates)
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// 解析解释器（顺序很重要）：
    ///   1. 用户在设置里显式指定的；
    ///   2. 应用数据目录下的专用 venv（%LOCALAPPDATA%\ScopeRecallWin\cloudsync-venv）——
    ///      这是安装时建好的推荐位置，**放在安装目录之外**，所以升级/重装应用不会把它删掉；
    ///   3. 应用目录旁的 .venv（开发态）；
    ///   4. 最后才回落插件的 shared-venv —— 它没有 webdav4/boto3，预检会明确报缺依赖。
    /// 注意必须**优先专用 venv**：插件的 shared-venv 装了 scope_recall 但没有同步依赖，
    /// 直接用它会让每一轮同步都失败。
    /// </summary>
    public string? ResolvePython(string memoryRoot)
    {
        var configured = App.Settings.CloudSyncPythonPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        foreach (var candidate in new[]
                 {
                     DedicatedVenvPython,
                     Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe"),
                     Path.Combine(AppContext.BaseDirectory, "..", ".venv", "Scripts", "python.exe"),
                 })
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return PathDiscovery.FindVenvPython(memoryRoot);
    }

    /// <summary>推荐安装位置：应用数据目录下的专用 venv（独立于安装目录，升级不丢）。</summary>
    public static string DedicatedVenvPython => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScopeRecallWin", "cloudsync-venv", "Scripts", "python.exe");

    public async Task<CloudSyncPreflight> PreflightAsync(string memoryRoot, CancellationToken ct = default)
    {
        var script = ResolveBridgeScript();
        var python = ResolvePython(memoryRoot);

        if (python is null)
        {
            return new CloudSyncPreflight(false, null, null, script is not null, script,
                false, Array.Empty<string>(),
                "未找到 Python 解释器。请在设置里指定专用 venv 的 python.exe，或按下方命令创建：\n" +
                $"  python -m venv \"{Path.GetDirectoryName(Path.GetDirectoryName(DedicatedVenvPython))}\"\n" +
                $"  \"{DedicatedVenvPython}\" -m pip install mcp==2.1.0 webdav4 boto3");
        }

        var versionResult = await ProcessRunner.RunAsync(python, "-V", timeoutMs: 20_000, cancellationToken: ct)
            .ConfigureAwait(false);
        var version = versionResult.Success ? versionResult.StdOut.Trim() : null;

        // 依赖探测：一次调用同时检查三个模块，避免三次进程启动
        const string probe = "-c \"import importlib.util as u;print(','.join(m for m in ['mcp','webdav4','boto3'] if not u.find_spec(m)))\"";
        var depResult = await ProcessRunner.RunAsync(python, probe, timeoutMs: 60_000, cancellationToken: ct)
            .ConfigureAwait(false);

        var missing = new List<string>();
        if (depResult.Success)
        {
            var text = depResult.StdOut.Trim().Trim('\'', '"');
            if (!string.IsNullOrWhiteSpace(text))
            {
                missing.AddRange(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        else
        {
            missing.AddRange(new[] { "mcp", "webdav4", "boto3" });
        }

        string? message = null;
        if (script is null)
        {
            message = "未找到桥脚本 cloudsync_run.py（应随应用一起分发在 bridge\\ 目录）。";
        }
        else if (missing.Count > 0)
        {
            message = $"该 Python 缺少依赖：{string.Join(" / ", missing)}。\n" +
                      $"请安装后重试（不要装进插件的 shared-venv）：\n  \"{python}\" -m pip install " +
                      string.Join(' ', missing);
        }

        return new CloudSyncPreflight(
            true, python, version, script is not null, script, missing.Count == 0, missing, message);
    }

    public async Task<CloudSyncRound> RunOnceAsync(
        string memoryRoot, string? backupPath, CancellationToken ct = default)
    {
        var script = ResolveBridgeScript()
            ?? throw new InvalidOperationException("未找到桥脚本 cloudsync_run.py。");
        var python = ResolvePython(memoryRoot)
            ?? throw new InvalidOperationException("未找到 Python 解释器。");
        var dbPath = PathDiscovery.MemoryDbPath(memoryRoot);
        if (!File.Exists(dbPath))
        {
            throw new InvalidOperationException($"未找到记忆库：{dbPath}");
        }

        var config = GetConfig();
        var statusPath = StatusPath(memoryRoot);
        var statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScopeRecallWin", "cloudsync-state.json");

        // 关键：跑桥用的是**专用 venv**（有 webdav4/boto3/mcp），但桥内部要调用记忆插件的
        // MCP 入口（propose_memory），那需要**装了 scope_recall 的插件 venv**。两者是不同的
        // 解释器，所以必须显式把插件解释器传给桥（--python），否则桥会用 sys.executable
        // 去跑插件入口并报 ModuleNotFoundError: scope_recall。
        var pluginPython = PathDiscovery.FindVenvPython(memoryRoot);
        if (pluginPython is null)
        {
            throw new InvalidOperationException("未找到共享运行环境（shared-venv），无法调用官方记忆接口。");
        }

        var args = BuildArgs(script, dbPath, memoryRoot, config, statusPath, statePath, pluginPython);

        // 口令经环境变量注入（绝不进命令行参数）。stdio 子进程环境从当前进程继承，
        // 这里用 ProcessRunner 的默认行为，故先在当前进程内临时设置再还原。
        var env = await BuildSecretEnvironmentAsync(config).ConfigureAwait(false);
        var previous = new Dictionary<string, string?>();
        foreach (var (key, value) in env)
        {
            previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                python, args, workingDirectory: Path.GetDirectoryName(script),
                timeoutMs: 600_000, cancellationToken: ct).ConfigureAwait(false);

            var round = ReadLastStatus(memoryRoot);
            if (round is not null)
            {
                return round;
            }

            // 子进程没产出状态文件时，用进程结果兜底，避免界面显示「空白」
            var errors = new List<string>();
            if (!result.Success)
            {
                errors.Add(MaintenanceCli.FailureText(result));
            }
            return new CloudSyncRound(
                result.Success ? "ok" : "failed", 0, 0, 0, 0, 0, 0, result.Duration.TotalSeconds,
                null, null, errors, 0, 0);
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>
    /// 拼装桥脚本参数。**注意：口令不在此处** —— 它走环境变量（见 BuildSecretEnvironmentAsync），
    /// 因为命令行参数对同机其他用户可见（进程列表）。
    /// </summary>
    internal static string BuildArgs(
        string script, string dbPath, string memoryRoot, CloudSyncConfig config,
        string statusPath, string statePath, string? pluginPython = null)
    {
        var parts = new List<string>
        {
            Quote(script),
            "--db", Quote(dbPath),
            "--plugin-home", Quote(CloudEntryHome(memoryRoot)),
            "--backend", config.Backend,
            "--state", Quote(statePath),
            "--status", Quote(statusPath),
            "--once",
        };

        // 插件解释器：桥内部要用它跑 propose_memory（见 RunOnceAsync 注释）
        if (!string.IsNullOrWhiteSpace(pluginPython))
        {
            parts.Add("--python");
            parts.Add(Quote(pluginPython));
        }

        if (!string.IsNullOrWhiteSpace(config.Root))
        {
            parts.Add("--root");
            parts.Add(Quote(config.Root));
        }
        if (string.Equals(config.Backend, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(config.Url))
            {
                parts.Add("--url");
                parts.Add(Quote(config.Url));
            }
            if (!string.IsNullOrWhiteSpace(config.User))
            {
                parts.Add("--user");
                parts.Add(Quote(config.User));
            }
        }
        else if (string.Equals(config.Backend, "s3", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(config.Bucket))
            {
                parts.Add("--bucket");
                parts.Add(Quote(config.Bucket));
            }
            if (!string.IsNullOrWhiteSpace(config.Endpoint))
            {
                parts.Add("--endpoint");
                parts.Add(Quote(config.Endpoint));
            }
        }

        if (!config.IncludeEvents)
        {
            parts.Add("--no-events");
        }
        return string.Join(' ', parts);
    }

    /// <summary>cloud entry 的家目录（attach 时由 --instance-root 指定）。</summary>
    public static string CloudEntryHome(string memoryRoot) =>
        Path.Combine(memoryRoot, "cloud-entry");

    /// <summary>
    /// 确保云桥成员已接入共享库。
    /// </summary>
    /// <remarks>
    /// 官方 attach 契约（本机实测）：
    /// <list type="bullet">
    /// <item><c>--root</c> 是**共享库目录**本身（其下 installation.json 即清单），不是记忆库根；</item>
    /// <item>客户端宿主的 <c>--capture-like</c> 必须指向**已接入的 Hermes 成员**（传 dsh 会被拒：
    /// "not attached Hermes entries of this store"），<c>--grants-like</c> 可用 all；</item>
    /// <item>合法 host 只有 hermes/codex/claude-code/workbuddy/dsh；桥用 codex —— 因为
    /// propose_memory 的线程元数据 <c>_THREAD_META</c> 只映射 codex，用别的 host 会
    /// ACCESS_DENIED: codex_thread_id。</item>
    /// </list>
    /// </remarks>
    public async Task<CloudEntryResult> EnsureCloudEntryAsync(string memoryRoot, CancellationToken ct = default)
    {
        var venvPython = PathDiscovery.FindVenvPython(memoryRoot);
        if (venvPython is null)
        {
            return new CloudEntryResult(false, false, "未找到共享运行环境（shared-venv），无法接入云桥成员。", null);
        }

        var sharedRoot = PathDiscovery.SharedDir(memoryRoot);
        var home = CloudEntryHome(memoryRoot);
        Directory.CreateDirectory(home);

        // 已接入则直接返回（幂等）
        var listResult = await ProcessRunner.RunAsync(
            venvPython, $"{MaintenanceCli.Module} entries --root {Quote(sharedRoot)}",
            timeoutMs: 120_000, cancellationToken: ct).ConfigureAwait(false);

        if (listResult.Success && listResult.StdOut.Contains("\"entry_id\": \"cloud\"", StringComparison.Ordinal))
        {
            return new CloudEntryResult(true, true, "云桥成员已在共享库中。", null);
        }

        // capture-like 必须是已接入的 Hermes 成员
        var hermesEntry = ExtractFirstHermesEntry(listResult.StdOut);
        if (hermesEntry is null)
        {
            return new CloudEntryResult(false, false,
                "共享库里没有 Hermes 成员：云桥成员的捕获范围要参照 Hermes 成员，请先到「共享存储」页接入一个 Hermes 助手。",
                null);
        }

        var result = await ProcessRunner.RunAsync(
            venvPython,
            MaintenanceCli.AttachArgs(
                sharedRoot, "codex", home, "cloud", "CloudSync",
                grantsLike: "all", captureLike: hermesEntry,
                runtimeConfigFrom: PathDiscovery.RuntimeConfigPath(memoryRoot)),
            timeoutMs: 600_000, cancellationToken: ct).ConfigureAwait(false);

        if (result.Success)
        {
            return new CloudEntryResult(true, false, $"云桥成员已接入（参照 Hermes 成员「{hermesEntry}」）。", result.StdOut);
        }
        return new CloudEntryResult(false, false, MaintenanceCli.FailureText(result), result.StdOut);
    }

    /// <summary>从 entries 输出里取第一个 Hermes 成员标识（简易解析，避免引 JsonDocument 依赖顺序）。</summary>
    internal static string? ExtractFirstHermesEntry(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var e in entries.EnumerateArray())
            {
                var host = e.TryGetProperty("host", out var h) ? h.GetString() : null;
                var id = e.TryGetProperty("entry_id", out var i) ? i.GetString() : null;
                var detached = e.TryGetProperty("detached_at", out var d) && d.ValueKind == JsonValueKind.String;
                if (string.Equals(host, "hermes", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(id) && !detached)
                {
                    return id;
                }
            }
        }
        catch
        {
            // 输出不是预期 JSON 时返回 null，由调用方给出可读提示
        }
        return null;
    }

    /// <summary>状态文件路径（供界面展示上一轮结果）。</summary>
    public static string StatusPath(string memoryRoot) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScopeRecallWin", "cloudsync-status.json");

    /// <summary>
    /// 远端 agent 链接状态：调用桥目录里的 remote_status.py（只读探针，与桥共用后端与凭据环境），
    /// 读云端账本并按 origin_agent 聚合。探针用**专用 venv** 解释器（有 webdav4/boto3）。
    /// </summary>
    public async Task<RemoteLinkStatus?> GetRemoteLinkStatusAsync(string memoryRoot, CancellationToken ct = default)
    {
        var bridgeScript = ResolveBridgeScript();
        var bridgeDir = bridgeScript is null ? null : Path.GetDirectoryName(bridgeScript);
        var probe = bridgeDir is null ? null : Path.Combine(bridgeDir, "remote_status.py");
        if (probe is null || !File.Exists(probe))
        {
            return new RemoteLinkStatus(false, "未找到探针脚本 remote_status.py（应随应用分发在 bridge\\ 目录）。",
                0, 0, 0, false, 0, null, Array.Empty<RemoteLinkWriter>(), ReadLastStatus(memoryRoot));
        }
        var python = DedicatedVenvPython;
        if (!File.Exists(python))
        {
            return new RemoteLinkStatus(false, "未找到专用 venv 解释器（远端探针需要 webdav4/boto3）。",
                0, 0, 0, false, 0, null, Array.Empty<RemoteLinkWriter>(), ReadLastStatus(memoryRoot));
        }

        var config = GetConfig();
        var parts = new List<string>
        {
            Quote(probe),
            "--backend", config.Backend,
            "--sample", "60",
        };
        if (!string.IsNullOrWhiteSpace(config.Root))
        {
            parts.Add("--root"); parts.Add(Quote(config.Root));
        }
        if (string.Equals(config.Backend, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(config.Url)) { parts.Add("--url"); parts.Add(Quote(config.Url)); }
            if (!string.IsNullOrWhiteSpace(config.User)) { parts.Add("--user"); parts.Add(Quote(config.User)); }
        }
        else if (string.Equals(config.Backend, "s3", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(config.Bucket)) { parts.Add("--bucket"); parts.Add(Quote(config.Bucket)); }
            if (!string.IsNullOrWhiteSpace(config.Endpoint)) { parts.Add("--endpoint"); parts.Add(Quote(config.Endpoint)); }
        }

        var env = await BuildSecretEnvironmentAsync(config).ConfigureAwait(false);
        var previous = new Dictionary<string, string?>();
        foreach (var (key, value) in env)
        {
            previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                python, string.Join(' ', parts), workingDirectory: bridgeDir,
                timeoutMs: 180_000, cancellationToken: ct).ConfigureAwait(false);
            var stdout = (result.StdOut ?? "").Trim();
            if (!stdout.StartsWith('{'))
            {
                return new RemoteLinkStatus(false,
                    MaintenanceCli.FailureText(result),
                    0, 0, 0, false, 0, null, Array.Empty<RemoteLinkWriter>(), ReadLastStatus(memoryRoot));
            }
            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            var e = doc.RootElement;

            var writers = new List<RemoteLinkWriter>();
            if (e.TryGetProperty("writers", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var w in arr.EnumerateArray())
                {
                    writers.Add(new RemoteLinkWriter(
                        Str(w, "agent") ?? "unknown",
                        Int(w, "records"),
                        Int(w, "claims"),
                        Int(w, "events"),
                        Double(w, "last_ts"),
                        Str(w, "last_iso")));
                }
            }
            var status = new RemoteLinkStatus(
                Str(e, "reachable") == "True" || (e.TryGetProperty("reachable", out var reach) && reach.ValueKind == System.Text.Json.JsonValueKind.True),
                Str(e, "error"),
                Int(e, "total_records"),
                Int(e, "claims"),
                Int(e, "events"),
                Str(e, "sampled") == "True",
                Int(e, "sample_size"),
                Str(e, "last_update_iso"),
                writers,
                ReadLastStatus(memoryRoot),
                Str(e, "fallback") == "True");
            return status;
        }
        catch (Exception ex)
        {
            return new RemoteLinkStatus(false, $"{ex.GetType().Name}: {ex.Message}",
                0, 0, 0, false, 0, null, Array.Empty<RemoteLinkWriter>(), ReadLastStatus(memoryRoot));
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>
    /// 组装子进程需要的云端凭据环境变量。
    /// 口令（WebDAV 应用密码 / S3 Secret Access Key）从 Windows 凭据管理器取；
    /// S3 的 Access Key ID 是标识符，直接取自配置（见 AppSettings.CloudSyncAccessKeyId）。
    /// </summary>
    internal async Task<Dictionary<string, string>> BuildSecretEnvironmentAsync(CloudSyncConfig config)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.Equals(config.Backend, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            if (_credentials is not null)
            {
                var account = config.User ?? "";
                var pwd = await _credentials.GetAsync(CredentialServiceName, account).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(pwd))
                {
                    env["SR_CLOUD_PWD"] = pwd;
                }
            }
        }
        else if (string.Equals(config.Backend, "s3", StringComparison.OrdinalIgnoreCase))
        {
            // Access Key ID 来自配置（非敏感标识符）；此前只从凭据管理器读，
            // 而没有任何地方写入过它 → S3 永远拿不到 access key。
            if (!string.IsNullOrWhiteSpace(config.AccessKeyId))
            {
                env["S3_ACCESS_KEY"] = config.AccessKeyId;
            }
            if (_credentials is not null)
            {
                var secret = await _credentials.GetAsync(CredentialServiceName, "s3-secret-key").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(secret))
                {
                    env["S3_SECRET_KEY"] = secret;
                }
            }
        }
        return env;
    }

    public CloudSyncRound? ReadLastStatus(string memoryRoot)
    {
        var path = StatusPath(memoryRoot);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var errors = new List<string>();
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in errs.EnumerateArray())
                {
                    var text = e.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        errors.Add(text);
                    }
                }
            }

            var stats = root.TryGetProperty("stats", out var st) ? st : default;
            return new CloudSyncRound(
                Str(root, "status"),
                Int(root, "pushed"),
                Int(root, "pulled"),
                Int(root, "written_to_store"),
                Int(root, "unchanged"),
                Int(root, "conflicts"),
                Int(root, "skipped_duplicates"),
                Double(root, "duration_seconds"),
                Str(root, "round_started"),
                Str(root, "written_at"),
                errors,
                stats.ValueKind == JsonValueKind.Object ? Int(stats, "claims_active") : 0,
                stats.ValueKind == JsonValueKind.Object ? Int(stats, "source_events") : 0);
        }
        catch
        {
            return null;
        }
    }

    // ---------- 内部 ----------

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double Double(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>
    /// 参数加引号。Windows 命令行里以反斜杠结尾的路径会转义后续引号，需成对加倍
    /// （与 MaintenanceCli.Quoted 同一理由）。
    /// </summary>
    private static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\\\"");
        if (escaped.EndsWith('\\'))
        {
            escaped += "\\";
        }
        return $"\"{escaped}\"";
    }
}
