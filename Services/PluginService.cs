using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ScopeRecallWin.Infrastructure;

namespace ScopeRecallWin.Services;

/// <summary>
/// 记忆插件（hermes-scope-recall）管理：环境探测、上游版本检查、一键部署、版本升级。
/// 全部动作经官方 CLI（shared-venv 的 python -m scope_recall.maintenance.cli …）完成，
/// 本服务只做编排、校验与进度汇总，不直接写记忆库。
/// </summary>
public interface IPluginService
{
    /// <summary>探测本机部署环境（根目录 / venv / 已装版本 / uv）。</summary>
    Task<PluginEnvironment> ProbeEnvironmentAsync(CancellationToken ct = default);

    /// <summary>检查上游最新版本并实测各下载通道延迟。</summary>
    Task<PluginUpstreamCheck> CheckUpstreamAsync(CancellationToken ct = default);

    /// <summary>部署计划预览（目标目录、将执行的步骤、磁盘占用预估）。</summary>
    Task<PluginInstallPlan> PlanInstallAsync(string root);

    /// <summary>执行部署（venv + wheel + init-shared + 权限自检；已有库则复用校验）。</summary>
    Task<PluginInstallResult> ApplyInstallAsync(
        string root, string authorization, IProgress<PluginInstallProgress>? progress = null, CancellationToken ct = default);

    /// <summary>列出共享库的 entries（home/host）。</summary>
    Task<IReadOnlyList<PluginEntry>> ListEntriesAsync(string root, CancellationToken ct = default);

    /// <summary>插件升级执行（六步：precheck→backup→download→quiesce→install→verify）。</summary>
    Task<PluginUpgradeResult> UpgradeAsync(
        PluginUpgradeRequest request, IProgress<PluginUpgradeProgress>? progress, CancellationToken ct = default);
}

public sealed record PluginEnvironment(
    string? Root,
    string? VenvPython,
    string? InstalledVersion,
    bool InstallationJsonExists,
    string? MemoryDbPath,
    long? MemoryDbSize,
    bool ExternalUvAvailable,
    string? Error);

public sealed record ChannelProbe(string Name, bool Ok, int LatencyMs, string? Error);

public sealed record PluginUpstreamCheck(
    string? InstalledVersion,
    string? LatestVersion,
    string? WheelUrl,
    string? WheelSha256,
    IReadOnlyList<ChannelProbe> ChannelProbes,
    bool HasUpdate,
    string? Error);

public sealed record PluginInstallPlan(
    string Root,
    IReadOnlyList<string> Steps,
    long? FreeDiskBytes,
    bool ReuseExisting,
    string? Warning);

public sealed record PluginInstallProgress(string Step, string State, string? Detail);

public sealed record PluginInstallResult(
    bool Success,
    string? InstalledVersion,
    IReadOnlyList<string> Steps,
    string? Error);

public sealed record PluginEntry(string Entry, string Host, string Home, string? Python, bool Alive, string? DisplayName = null);

public sealed record PluginUpgradeRequest(
    string Root,
    string Authorization,
    bool Confirm,
    string Channel,
    bool Force);

public sealed record PluginUpgradeProgress(
    string Step,
    string State,
    string? Detail,
    int StepIndex,
    int StepCount);

public sealed record PluginUpgradeResult(
    bool Success,
    string? FromVersion,
    string? ToVersion,
    IReadOnlyList<string> Log,
    string? Error);

/// <summary>
/// 插件管理服务实现。探测/检查/部署/升级全部走官方 CLI 编排；
/// 升级流程与 WebUI 控制台 plugin_upgrade.py 的六步一致（precheck→backup→download→
/// quiesce→install→verify），护栏：confirm=true + 授权句 ≥6 字 + 仅本机来源。
/// </summary>
public sealed class PluginService : IPluginService
{
    public const string PackageName = "hermes-scope-recall";
    public const string PypiProjectUrl = "https://pypi.org/pypi/hermes-scope-recall/json";
    public const string WorkDirName = "_upgrade-console";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // ---------- 探测 ----------

    public async Task<PluginEnvironment> ProbeEnvironmentAsync(CancellationToken ct = default)
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null)
        {
            return new PluginEnvironment(null, null, null, false, null, null, false,
                "未找到记忆库根目录（shared\\installation.json）。请先在「安装」页选择部署目录。");
        }

        var venvPython = PathDiscovery.FindVenvPython(root);
        string? installed = null;
        if (venvPython is not null)
        {
            var r = await ProcessRunner.RunAsync(
                venvPython,
                $"-c \"import importlib.metadata as m; print(m.version('{PackageName}'))\"",
                timeoutMs: 30_000, cancellationToken: ct).ConfigureAwait(false);
            installed = r.Success ? r.StdOut.Trim() : null;
        }

        var dbPath = PathDiscovery.MemoryDbPath(root);
        long? dbSize = null;
        try
        {
            if (File.Exists(dbPath))
            {
                dbSize = new FileInfo(dbPath).Length;
            }
        }
        catch
        {
            // 文件不可访问时忽略大小
        }

        var uv = await ProcessRunner.RunAsync("uv", "--version", timeoutMs: 10_000, cancellationToken: ct)
            .ConfigureAwait(false);

        return new PluginEnvironment(
            root, venvPython, installed,
            File.Exists(PathDiscovery.InstallationJsonPath(root)),
            File.Exists(dbPath) ? dbPath : null,
            dbSize, uv.Success, null);
    }

    // ---------- 上游检查 ----------

    public async Task<PluginUpstreamCheck> CheckUpstreamAsync(CancellationToken ct = default)
    {
        var env = await ProbeEnvironmentAsync(ct).ConfigureAwait(false);
        var probes = await ProbeChannelsAsync(ct).ConfigureAwait(false);

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, PypiProjectUrl);
            req.Headers.UserAgent.ParseAdd("ScopeRecallWin/1.0");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new PluginUpstreamCheck(env.InstalledVersion, null, null, null, probes, false,
                    $"PyPI HTTP {(int)resp.StatusCode}");
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var latest = root.GetProperty("info").GetProperty("version").GetString();
            string? wheelUrl = null;
            string? sha = null;
            if (root.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in urls.EnumerateArray())
                {
                    var fn = u.GetProperty("filename").GetString() ?? "";
                    if (fn.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
                    {
                        wheelUrl = u.GetProperty("url").GetString();
                        if (u.TryGetProperty("digests", out var d)
                            && d.TryGetProperty("sha256", out var s))
                        {
                            sha = s.GetString();
                        }
                        break;
                    }
                }
            }

            var hasUpdate = env.InstalledVersion is not null && latest is not null
                && UpdateService.IsNewerThan(latest, env.InstalledVersion);

            return new PluginUpstreamCheck(env.InstalledVersion, latest, wheelUrl, sha, probes, hasUpdate, null);
        }
        catch (Exception ex)
        {
            return new PluginUpstreamCheck(env.InstalledVersion, null, null, null, probes, false, ex.Message);
        }
    }

    // ---------- entries ----------

    public async Task<IReadOnlyList<PluginEntry>> ListEntriesAsync(string root, CancellationToken ct = default)
    {
        var result = new List<PluginEntry>();
        var jsonPath = PathDiscovery.InstallationJsonPath(root);
        if (!File.Exists(jsonPath))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("entries", out var entries))
            {
                // 上游结构为数组：[{"entry_id": "dsh", "host": "dsh", "home": "…",
                //                  "display_name": "DeepSeek Harness", "python_executable": "…"}]
                // 早期误按对象解析导致 entry 列表恒为空（连带 Agent 链接状态/entries 台账/
                // 诊断/升级停机全部静默失效），此处数组与对象两种形态都兼容。
                if (entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in entries.EnumerateArray())
                    {
                        var id = Str(el, "entry_id");
                        if (id.Length == 0)
                        {
                            continue;
                        }
                        result.Add(new PluginEntry(
                            id,
                            Str(el, "host"),
                            Str(el, "home"),
                            Str(el, "python_executable") is { Length: > 0 } pe ? pe : Nullable(Str(el, "python")),
                            Directory.Exists(Str(el, "home")),
                            Nullable(Str(el, "display_name"))));
                    }
                }
                else if (entries.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in entries.EnumerateObject())
                    {
                        result.Add(new PluginEntry(
                            prop.Name,
                            Str(prop.Value, "host"),
                            Str(prop.Value, "home"),
                            Nullable(Str(prop.Value, "python")),
                            Directory.Exists(Str(prop.Value, "home")),
                            Nullable(Str(prop.Value, "display_name"))));
                    }
                }
            }
        }
        catch
        {
            // installation.json 损坏时返回空列表
        }

        // 常驻服务探测：端点注册表记录形如
        //   %LOCALAPPDATA%\scope-recall\hook-endpoints\<sha16(home)>\<pid>.json
        //   {"host": "workbuddy", "port": 53030, "pid": 16056, "version": "3.10.2", "resident": true}
        // 注意：记录里**没有 home 字段**，早期按 home 匹配导致 Alive 恒为 false；
        // 这里改为按 host 匹配 + 校验 pid 进程是否仍存活（与 AgentLinkService 口径一致）。
        try
        {
            var endpoints = new List<(string Host, int Pid)>();
            var endpointDir = PathDiscovery.HookEndpointsDir;
            if (Directory.Exists(endpointDir))
            {
                foreach (var f in Directory.EnumerateFiles(endpointDir, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        using var d = JsonDocument.Parse(File.ReadAllText(f));
                        var host = d.RootElement.TryGetProperty("host", out var hh) ? hh.GetString() ?? "" : "";
                        var pid = d.RootElement.TryGetProperty("pid", out var pp) ? pp.GetInt32() : 0;
                        if (host.Length > 0 && pid > 0)
                        {
                            endpoints.Add((host, pid));
                        }
                    }
                    catch
                    {
                        // 单个端点文件损坏时跳过
                    }
                }
            }

            for (int i = 0; i < result.Count; i++)
            {
                var host = result[i].Host;
                var alive = endpoints.Any(e =>
                    string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) && IsProcessAlive(e.Pid));
                result[i] = result[i] with { Alive = alive };
            }
        }
        catch
        {
            // 注册表不可读时保持默认（未运行）
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    // ---------- 部署 ----------

    public Task<PluginInstallPlan> PlanInstallAsync(string root)
    {
        var reuse = PathDiscovery.IsMemoryRoot(root);
        var steps = new List<string>();
        if (reuse)
        {
            steps.Add("检测到已有记忆库（installation.json 存在）→ 复用模式");
            steps.Add("核对已装版本与官方 CLI 可用性");
            steps.Add("doctor 校验 + 目录权限自检（控制台与 Agent 双方可读写）");
        }
        else
        {
            steps.Add("创建目录结构：shared\\ 与 shared-venv\\");
            steps.Add("创建共享 venv 并安装 hermes-scope-recall（含依赖，约 1.5GB）");
            steps.Add("init-shared 初始化记忆库（installation.json）");
            steps.Add("目录权限自检（控制台与 Agent 双方可读写）");
        }

        long? free = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? "C:\\");
            free = drive.AvailableFreeSpace;
        }
        catch
        {
            // 路径不可解析时跳过
        }

        return Task.FromResult(new PluginInstallPlan(
            root, steps, free, reuse,
            reuse ? null : "全新部署约需 1.5GB 磁盘（venv + 依赖），安装依赖需要数分钟。"));
    }

    public async Task<PluginInstallResult> ApplyInstallAsync(
        string root, string authorization, IProgress<PluginInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorization) || authorization.Trim().Length < 6)
        {
            return new PluginInstallResult(false, null, Array.Empty<string>(),
                "授权句不足 6 字：请用一句话确认你本人了解本次部署（将写入所选目录）。");
        }

        var steps = new List<string>();
        try
        {
            Directory.CreateDirectory(root);

            if (PathDiscovery.IsMemoryRoot(root))
            {
                // ---------- 复用模式：校验 + 权限自检 ----------
                progress?.Report(new PluginInstallProgress("校验已有记忆库", "running", root));
                steps.Add($"复用已有记忆库：{root}");
                var venv = PathDiscovery.FindVenvPython(root);
                if (venv is null)
                {
                    return new PluginInstallResult(false, null, steps,
                        $"未在 {root} 下找到 shared-venv\\Scripts\\python.exe，无法校验。");
                }
                var doctor = await RunDoctorAsync(venv, root, ct).ConfigureAwait(false);
                steps.Add($"doctor：{doctor.State} {doctor.Detail}");
                if (doctor.State == "failed")
                {
                    return new PluginInstallResult(false, null, steps, $"doctor 校验失败：{doctor.Detail}");
                }
                var acl = CheckDirectoryAccess(root);
                steps.Add(acl);
                progress?.Report(new PluginInstallProgress("校验已有记忆库", "done", doctor.State));
                return new PluginInstallResult(true, await InstalledVersionAsync(venv, ct).ConfigureAwait(false),
                    steps, acl.Contains("警告") ? acl : null);
            }

            // ---------- 全新部署 ----------
            progress?.Report(new PluginInstallProgress("创建目录", "running", root));
            var shared = PathDiscovery.SharedDir(root);
            Directory.CreateDirectory(shared);
            var venvDir = Path.Combine(root, PathDiscovery.VenvDirName);
            steps.Add($"创建目录：{shared}");
            progress?.Report(new PluginInstallProgress("创建目录", "done", null));

            progress?.Report(new PluginInstallProgress("创建共享 venv", "running", venvDir));
            var basePython = await FindBasePythonAsync(ct).ConfigureAwait(false);
            if (basePython is null)
            {
                return new PluginInstallResult(false, null, steps,
                    "未找到可用的 Python 3.12+ 解释器（PATH 中无 python）。请先安装 Python 或在 PATH 中配置。");
            }
            var venvResult = await ProcessRunner.RunAsync(
                basePython, $"-m venv \"{venvDir}\"", timeoutMs: 180_000, cancellationToken: ct).ConfigureAwait(false);
            if (!venvResult.Success)
            {
                return new PluginInstallResult(false, null, steps,
                    $"venv 创建失败：{FirstLine(venvResult.StdErr) ?? venvResult.StdOut}");
            }
            steps.Add("创建共享 venv 完成");
            progress?.Report(new PluginInstallProgress("创建共享 venv", "done", null));

            var venvPython = Path.Combine(venvDir, "Scripts", "python.exe");
            if (!File.Exists(venvPython))
            {
                return new PluginInstallResult(false, null, steps, "venv 创建后未找到 Scripts\\python.exe。");
            }

            progress?.Report(new PluginInstallProgress("安装插件 wheel", "running",
                "pip install hermes-scope-recall（含依赖，可能需要数分钟）"));
            var installArgs = $"-m pip install --disable-pip-version-check {PackageName}";
            var install = await ProcessRunner.RunAsync(
                venvPython, installArgs, timeoutMs: 1_800_000, cancellationToken: ct).ConfigureAwait(false);
            if (!install.Success)
            {
                return new PluginInstallResult(false, null, steps,
                    $"插件安装失败：{FirstLine(install.StdErr) ?? install.StdOut}");
            }
            steps.Add("安装 hermes-scope-recall 完成");
            progress?.Report(new PluginInstallProgress("安装插件 wheel", "done", null));

            progress?.Report(new PluginInstallProgress("初始化记忆库", "running", "init-shared"));
            var init = await ProcessRunner.RunAsync(
                venvPython,
                $"-m scope_recall.maintenance.cli init-shared --root \"{root}\"",
                timeoutMs: 300_000, cancellationToken: ct).ConfigureAwait(false);
            if (!init.Success)
            {
                return new PluginInstallResult(false, null, steps,
                    $"init-shared 失败：{FirstLine(init.StdErr) ?? init.StdOut}");
            }
            steps.Add("init-shared 完成（installation.json 已生成）");
            progress?.Report(new PluginInstallProgress("初始化记忆库", "done", null));

            var aclMessage = CheckDirectoryAccess(root);
            steps.Add(aclMessage);
            var version = await InstalledVersionAsync(venvPython, ct).ConfigureAwait(false);
            return new PluginInstallResult(true, version, steps, aclMessage.Contains("警告") ? aclMessage : null);
        }
        catch (Exception ex)
        {
            return new PluginInstallResult(false, null, steps, ex.Message);
        }
    }

    // ---------- 升级（六步） ----------

    public async Task<PluginUpgradeResult> UpgradeAsync(
        PluginUpgradeRequest request, IProgress<PluginUpgradeProgress>? progress, CancellationToken ct = default)
    {
        var log = new List<string>();
        if (!request.Confirm)
        {
            return new PluginUpgradeResult(false, null, null, log, "未确认：请勾选确认框（confirm=true）。");
        }
        if (string.IsNullOrWhiteSpace(request.Authorization) || request.Authorization.Trim().Length < 6)
        {
            return new PluginUpgradeResult(false, null, null, log,
                "授权句不足 6 字：请用一句话确认你本人了解本次升级（将暂停 resident 服务并替换插件文件）。");
        }

        var root = request.Root;
        var venvPython = PathDiscovery.FindVenvPython(root);
        if (venvPython is null)
        {
            return new PluginUpgradeResult(false, null, null, log, $"未找到共享 venv：{root}");
        }

        var from = await InstalledVersionAsync(venvPython, ct).ConfigureAwait(false);
        var workDir = Path.Combine(root, WorkDirName);
        Directory.CreateDirectory(Path.Combine(workDir, "backups"));
        Directory.CreateDirectory(Path.Combine(workDir, "logs"));

        const int total = 6;
        void Report(int i, string step, string state, string? detail)
        {
            log.Add($"[{step}] {state} {detail}".Trim());
            progress?.Report(new PluginUpgradeProgress(step, state, detail, i, total));
        }

        // 1) precheck
        Report(1, "precheck", "running", "检查上游版本");
        var check = await CheckUpstreamAsync(ct).ConfigureAwait(false);
        if (check.Error is not null)
        {
            Report(1, "precheck", "failed", check.Error);
            return new PluginUpgradeResult(false, from, null, log, check.Error);
        }
        if (!check.HasUpdate && !request.Force)
        {
            Report(1, "precheck", "done", $"已是最新（{check.InstalledVersion}），零写入");
            return new PluginUpgradeResult(false, from, check.InstalledVersion, log,
                "已是最新版本，无需升级（如需强制重装请勾选 force）。");
        }
        if (check.WheelUrl is null || check.LatestVersion is null)
        {
            Report(1, "precheck", "failed", "未获取到 wheel 地址");
            return new PluginUpgradeResult(false, from, null, log, "上游未返回可用 wheel。");
        }
        Report(1, "precheck", "done", $"{check.InstalledVersion} → {check.LatestVersion}");

        // 2) backup
        Report(2, "backup", "running", "备份记忆库");
        var dbPath = PathDiscovery.MemoryDbPath(root);
        if (!File.Exists(dbPath))
        {
            Report(2, "backup", "failed", "memory.sqlite3 不存在");
            return new PluginUpgradeResult(false, from, null, log, "未找到 memory.sqlite3，中止（不碰未知状态）。");
        }
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var backupFile = Path.Combine(workDir, "backups", $"memory-{stamp}.sqlite3");
        var backup = await ProcessRunner.RunAsync(
            venvPython,
            $"-m scope_recall.maintenance.cli backup --database \"{dbPath}\" --output \"{backupFile}\"",
            timeoutMs: 600_000, cancellationToken: ct).ConfigureAwait(false);
        if (!backup.Success || !File.Exists(backupFile))
        {
            Report(2, "backup", "failed", FirstLine(backup.StdErr) ?? backup.StdOut);
            return new PluginUpgradeResult(false, from, null, log, "记忆库备份失败，中止升级。");
        }
        Report(2, "backup", "done", backupFile);

        // 3) download
        Report(3, "download", "running", "下载 wheel 并校验 sha256");
        var wheelPath = Path.Combine(workDir, $"hermes_scope_recall-{check.LatestVersion}-py3-none-any.whl");
        var dlError = await DownloadWheelAsync(check.WheelUrl, check.WheelSha256, wheelPath, ct).ConfigureAwait(false);
        if (dlError is not null)
        {
            Report(3, "download", "failed", dlError);
            return new PluginUpgradeResult(false, from, null, log, dlError);
        }
        Report(3, "download", "done", wheelPath);

        // 4) quiesce：停全部 entry 的 resident
        Report(4, "quiesce", "running", "暂停 resident 服务");
        var entries = await ListEntriesAsync(root, ct).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Host, "hermes", StringComparison.OrdinalIgnoreCase))
            {
                // hermes 走进程内导入，无 resident 子进程
                continue;
            }
            var stop = await ProcessRunner.RunAsync(
                venvPython,
                $"-m scope_recall.maintenance.cli resident --home \"{entry.Home}\" --host {entry.Host} stop",
                timeoutMs: 60_000, cancellationToken: ct).ConfigureAwait(false);
            log.Add($"[quiesce] resident stop {entry.Entry}({entry.Host}) exit={stop.ExitCode}");
        }
        Report(4, "quiesce", "done", $"{entries.Count} 个 entry 已处理");

        // 5) install：package-upgrade 离线换包
        Report(5, "install", "running", "package-upgrade 换包");
        var uvPath = ExternalUvPath;
        if (uvPath is null)
        {
            Report(5, "install", "failed", "未找到外部 uv 可执行文件");
            return new PluginUpgradeResult(false, from, null, log,
                "缺少外部 uv（package-upgrade 需要），请先安装 uv 后重试。已停的服务保持停止、备份保留。");
        }
        var pkgBackup = Path.Combine(workDir, $"pkg-backup-{stamp}");
        var upgrade = await ProcessRunner.RunAsync(
            venvPython,
            $"-m scope_recall.maintenance.cli package-upgrade --python \"{venvPython}\" --wheel \"{wheelPath}\" " +
            $"--backup \"{pkgBackup}\" --uv \"{uvPath}\" --source-quiesced",
            timeoutMs: 1_800_000, cancellationToken: ct).ConfigureAwait(false);
        if (!upgrade.Success)
        {
            Report(5, "install", "failed", FirstLine(upgrade.StdErr) ?? upgrade.StdOut);
            return new PluginUpgradeResult(false, from, null, log,
                $"换包失败（未自动回滚）：{FirstLine(upgrade.StdErr) ?? upgrade.StdOut}。备份保留于 {pkgBackup}。");
        }
        Report(5, "install", "done", pkgBackup);

        // 6) verify：版本 + doctor
        Report(6, "verify", "running", "doctor 校验");
        var nowVersion = await InstalledVersionAsync(venvPython, ct).ConfigureAwait(false);
        if (nowVersion != check.LatestVersion)
        {
            Report(6, "verify", "failed", $"版本不符：{nowVersion} ≠ {check.LatestVersion}");
            return new PluginUpgradeResult(false, from, null, log, "换包后版本不符，请检查或回滚。");
        }
        var doctor = await RunDoctorAsync(venvPython, root, ct).ConfigureAwait(false);
        Report(6, "verify", doctor.State == "failed" ? "failed" : "done", doctor.Detail);
        if (doctor.State == "failed")
        {
            return new PluginUpgradeResult(false, from, nowVersion, log, $"doctor 校验失败：{doctor.Detail}");
        }

        return new PluginUpgradeResult(true, from, nowVersion, log, null);
    }

    // ---------- 内部 ----------

    /// <summary>外部 uv 绝对路径（package-upgrade 要求原生可执行文件）。</summary>
    private static string? ExternalUvPath
    {
        get
        {
            try
            {
                var where = ProcessRunner.RunAsync("where.exe", "uv", timeoutMs: 5_000).GetAwaiter().GetResult();
                if (where.Success)
                {
                    var first = where.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (first.Length > 0 && File.Exists(first[0].Trim()))
                    {
                        return first[0].Trim();
                    }
                }
            }
            catch
            {
                // where 不可用时忽略
            }
            return null;
        }
    }

    private async Task<string?> InstalledVersionAsync(string venvPython, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(
            venvPython,
            $"-c \"import importlib.metadata as m; print(m.version('{PackageName}'))\"",
            timeoutMs: 30_000, cancellationToken: ct).ConfigureAwait(false);
        return r.Success ? r.StdOut.Trim() : null;
    }

    private static async Task<string?> FindBasePythonAsync(CancellationToken ct)
    {
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            var r = await ProcessRunner.RunAsync(candidate, "--version", timeoutMs: 10_000, cancellationToken: ct)
                .ConfigureAwait(false);
            if (r.Success && (r.StdOut + r.StdErr).Contains("Python 3"))
            {
                return candidate;
            }
        }
        return null;
    }

    private async Task<(string State, string Detail)> RunDoctorAsync(string venvPython, string root, CancellationToken ct)
    {
        // doctor 需要一个 entry 的 instance-root；取第一个 entry，无 entry 时用 shared 根
        var entries = await ListEntriesAsync(root, ct).ConfigureAwait(false);
        var first = entries.Count > 0 ? entries[0] : null;
        var host = first?.Host is { Length: > 0 } h ? h : "codex";
        var instanceRoot = first?.Home is { Length: > 0 } home ? home : PathDiscovery.SharedDir(root);
        var r = await ProcessRunner.RunAsync(
            venvPython,
            $"-m scope_recall.maintenance.cli doctor --host {host} --instance-root \"{instanceRoot}\" --python \"{venvPython}\"",
            timeoutMs: 180_000, cancellationToken: ct).ConfigureAwait(false);
        if (!r.Success)
        {
            return ("failed", FirstLine(r.StdErr) ?? FirstLine(r.StdOut) ?? $"exit={r.ExitCode}");
        }
        var text = r.StdOut;
        var status = "ok";
        if (text.Contains("degraded", StringComparison.OrdinalIgnoreCase))
        {
            status = "degraded";
        }
        return (status, FirstLine(text) ?? "doctor 完成");
    }

    /// <summary>目录访问自检：当前用户可读写；提示 Agent 侧可能的账户差异。</summary>
    private static string CheckDirectoryAccess(string root)
    {
        var shared = PathDiscovery.SharedDir(root);
        var writable = PathDiscovery.IsWritableByCurrentUser(shared);
        if (writable)
        {
            return "权限自检通过：当前用户对 shared\\ 可读写（控制台与 Agent 同账户运行时满足双向访问）。";
        }
        return "警告：当前用户对 shared\\ 不可写。若 Agent 以其他 Windows 账户运行，请以管理员身份为该账户授予读写权限后重试。";
    }

    private static async Task<string?> DownloadWheelAsync(
        string url, string? expectedSha256, string target, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("ScopeRecallWin/1.0");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(target);
            await source.CopyToAsync(file, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(expectedSha256))
            {
                var actual = UpdateService.ComputeSha256(target);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(target);
                    return $"wheel SHA256 校验失败（期望 {expectedSha256}，实际 {actual}）";
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static async Task<IReadOnlyList<ChannelProbe>> ProbeChannelsAsync(CancellationToken ct)
    {
        var targets = new (string Name, string Url)[]
        {
            ("PyPI", PypiProjectUrl),
            ("gh-proxy(PyPI)", "https://gh-proxy.com/https://pypi.org/pypi/hermes-scope-recall/json"),
            ("jsDelivr", "https://cdn.jsdelivr.net/gh/410979729/scope-recall-hermes@main/README.md"),
            ("gh-proxy(GitHub)", "https://gh-proxy.com/https://github.com/410979729/scope-recall-hermes"),
        };

        var results = new List<ChannelProbe>();
        foreach (var (name, url) in targets)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("ScopeRecallWin/1.0");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                sw.Stop();
                results.Add(new ChannelProbe(name, resp.IsSuccessStatusCode, (int)sw.ElapsedMilliseconds, null));
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new ChannelProbe(name, false, (int)sw.ElapsedMilliseconds, ex.Message));
            }
        }
        return results;
    }

    /// <summary>进程是否仍存活（用于常驻服务状态判定）。</summary>
    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取 JSON 对象里的字符串字段（缺失返回空串）。</summary>
    private static string Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>空串转 null（用于可选字段）。</summary>
    private static string? Nullable(string value) => value.Length == 0 ? null : value;

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = text.Split('\n')[0].Trim();
        return line.Length == 0 ? null : line;
    }
}
