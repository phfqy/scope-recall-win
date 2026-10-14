using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScopeRecallWin.Infrastructure;

/// <summary>
/// 官方维护 CLI（<c>python -m scope_recall.maintenance.cli</c>）的**唯一**参数拼装与回执解读处。
/// </summary>
/// <remarks>
/// 本机实测契约（上游 hermes_scope_recall 3.10.2，<c>scope_recall/maintenance/*.py</c>）：
/// <list type="number">
/// <item><b>绑定 store 的六个子命令必须给 --config</b> —— repair-claim-frames /
/// retire-rootless-claims / requalify / unindex-withheld-outputs / retry-failures /
/// respace-embeddings 的 <c>--config</c> 是 argparse <c>required=True</c>：漏传即 <c>exit 2</c>
/// 只打 usage（曾表现为「重试失败（退出码 2）：usage: …」）。</item>
/// <item>这些命令一律<b>默认预演</b>：不带 <c>--apply</c> 时退出码仍为 0，但回执
/// <c>"applied": false</c>，数据没动 —— 只看退出码会得到假成功。respace-embeddings 还要动作开关，
/// 缺省是只读 <c>status</c>，真重建须 <c>--start</c>。</item>
/// <item><b>共享库命令的 --root 是「共享库目录」本身</b>（即 <c>&lt;记忆库根&gt;\shared</c>，
/// 其下 installation.json 就是清单），不是记忆库根：<c>read_shared_payload(root)</c> 读
/// <c>root/installation.json</c>，<c>init-shared</c> 还把清单写进 root 并要求该目录全空。
/// 传记忆库根会得到 <c>{"error": "shared store manifest is required"}</c> 或
/// <c>{"error": "root must be a new or empty directory"}</c>，均 exit 2。</item>
/// <item>客户端宿主（workbuddy / dsh / codex / claude-code）接入时<b>必须给 --grants-like 与
/// --capture-like</b>，且两者只能指向该共享库中**已接入的 Hermes 成员**（客户端自身的授权就是从
/// Hermes 成员的 owner 行推导出来的）；Hermes 宿主反而必须**不给**这两个开关。合法 host 取值只有
/// hermes/codex/claude-code/workbuddy/dsh —— <c>workbuddy-ai</c> 是**成员标识**不是 host。</item>
/// <item>官方 CLI 的<b>业务错误以 JSON 写在 stdout</b>（<c>{"status":"error","error":…}</c>），
/// 只有 argparse 的用法错误才走 stderr。故展示失败原因必须先看 stderr，再看 stdout 的 JSON，
/// 否则界面上只会显示一个「{」。</item>
/// </list>
/// 结论：所有 CLI 调用经本类拼装（verify_static.py V8 会拦手写），确需手写时先读本节。
/// </remarks>
public static class MaintenanceCli
{
    /// <summary>模块调用前缀。</summary>
    public const string Module = "-m scope_recall.maintenance.cli";

    /// <summary>必须显式给出 --config 的子命令（上游 argparse required=True）。</summary>
    public static readonly IReadOnlySet<string> ConfigBoundCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "repair-claim-frames",
        "retire-rootless-claims",
        "requalify",
        "unindex-withheld-outputs",
        "retry-failures",
        "respace-embeddings",
    };

    /// <summary>合法宿主类型（上游 ENTRY_HOSTS）。注意 workbuddy-ai 是成员标识，不是宿主。</summary>
    public static readonly IReadOnlySet<string> EntryHosts = new HashSet<string>(StringComparer.Ordinal)
    {
        "hermes", "codex", "claude-code", "workbuddy", "dsh",
    };

    /// <summary>该子命令是否需要 --config 绑定 store（不需要者各有专用拼装方法）。</summary>
    public static bool RequiresConfig(string command) => ConfigBoundCommands.Contains(command);

    /// <summary>客户端宿主（需要 --grants-like / --capture-like；hermes 反而不能给）。</summary>
    public static bool IsClientHost(string host) => EntryHosts.Contains(host) && host != "hermes";

    // ---------- 参数拼装 ----------

    /// <summary>
    /// 拼装一条绑定 store 的维护命令。<paramref name="apply"/> 为 false 时只做预演，不落库。
    /// </summary>
    /// <param name="memoryRoot">记忆库根目录（其下 shared\runtime-config.json 即 --config 取值）。</param>
    /// <param name="command">子命令名，须在 <see cref="ConfigBoundCommands"/> 内。</param>
    /// <param name="apply">true 传 --apply（真落库）；false 为预演。</param>
    /// <param name="extra">追加开关，如 respace-embeddings 的 --start。</param>
    public static string Build(string memoryRoot, string command, bool apply, string? extra = null)
    {
        if (!ConfigBoundCommands.Contains(command))
        {
            throw new ArgumentException($"子命令 {command} 不需要 --config，请改用对应专用方法", nameof(command));
        }
        var args = $"{Module} {command} --config {Quoted(PathDiscovery.RuntimeConfigPath(memoryRoot))}";
        if (!string.IsNullOrWhiteSpace(extra))
        {
            args += " " + extra.Trim();
        }
        return apply ? args + " --apply" : args;
    }

    /// <summary>respace-embeddings 的动作开关：缺省不传即只读 status，要真重建须 --start。</summary>
    public static string? ActionSwitch(string command) =>
        command == "respace-embeddings" ? "--start" : null;

    /// <summary>init-shared：<paramref name="sharedRoot"/> 必须是**共享库目录本身**且为新建/空目录。</summary>
    public static string InitSharedArgs(string sharedRoot) =>
        $"{Module} init-shared --root {Quoted(sharedRoot)}";

    /// <summary>
    /// attach：接入一个成员。<paramref name="sharedRoot"/> 是共享库目录（非记忆库根）；
    /// 客户端宿主由调用方给出 <paramref name="grantsLike"/> / <paramref name="captureLike"/>
    /// （必须指向已接入的 Hermes 成员），Hermes 宿主传 null。
    /// </summary>
    public static string AttachArgs(
        string sharedRoot,
        string host,
        string instanceRoot,
        string entry,
        string displayName,
        string? grantsLike = null,
        string? captureLike = null,
        string? runtimeConfigFrom = null)
    {
        var args = $"{Module} attach --host {host} --instance-root {Quoted(instanceRoot)} " +
                   $"--root {Quoted(sharedRoot)} --entry {entry} --display-name {Quoted(displayName)}";
        if (!string.IsNullOrWhiteSpace(grantsLike))
        {
            args += $" --grants-like {grantsLike}";
        }
        if (!string.IsNullOrWhiteSpace(captureLike))
        {
            args += $" --capture-like {captureLike}";
        }
        if (!string.IsNullOrWhiteSpace(runtimeConfigFrom))
        {
            args += $" --runtime-config-from {Quoted(runtimeConfigFrom)}";
        }
        return args;
    }

    /// <summary>backup：不吃 --config，直接给库文件与输出文件。</summary>
    public static string BackupArgs(string dbPath, string outputPath) =>
        $"{Module} backup --database {Quoted(dbPath)} --output {Quoted(outputPath)}";

    /// <summary>rollback：不吃 --config；不带 apply 为预演校验。</summary>
    public static string RollbackArgs(string dbPath, string snapshotPath, bool apply)
    {
        var args = $"{Module} rollback --current-db {Quoted(dbPath)} --snapshot {Quoted(snapshotPath)}";
        return apply ? args + " --apply" : args;
    }

    /// <summary>doctor：按 entry 的家目录体检。</summary>
    public static string DoctorArgs(string host, string instanceRoot, string venvPython) =>
        $"{Module} doctor --host {host} --instance-root {Quoted(instanceRoot)} --python {Quoted(venvPython)}";

    /// <summary>resident stop：停某个 entry 的常驻服务（hermes 无 resident，勿调用）。</summary>
    public static string ResidentStopArgs(string home, string host) =>
        $"{Module} resident --home {Quoted(home)} --host {host} stop";

    /// <summary>package-upgrade：离线换包，--backup / --uv 均为上游必需项。</summary>
    public static string PackageUpgradeArgs(
        string venvPython, string wheelPath, string backupDir, string uvPath, bool sourceQuiesced)
    {
        var args = $"{Module} package-upgrade --python {Quoted(venvPython)} --wheel {Quoted(wheelPath)} " +
                   $"--backup {Quoted(backupDir)} --uv {Quoted(uvPath)}";
        return sourceQuiesced ? args + " --source-quiesced" : args;
    }

    // ---------- 回执解读 ----------

    /// <summary>
    /// 回执是否为「只预演、未落库」。CLI 预演时仍以退出码 0 结束，仅回执 JSON 里
    /// <c>"applied": false</c>，故写操作的成功提示前必须过这一关，避免假成功。
    /// </summary>
    public static bool IsPreviewOnly(string stdout) =>
        stdout.Contains("\"applied\": false", StringComparison.Ordinal);

    /// <summary>取回执顶层整数字段（如 retried / applied 计数）；缺失返回 null。</summary>
    public static int? ReceiptCount(string stdout, string field)
    {
        var match = Regex.Match(stdout, "\"" + Regex.Escape(field) + "\"\\s*:\\s*(\\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    /// <summary>取回执顶层字符串字段（如 status / error / code）；缺失返回 null。</summary>
    public static string? ReceiptText(string stdout, string field)
    {
        var match = Regex.Match(stdout, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 把一次 CLI 失败翻译成能给用户看的一句话。顺序：stderr 首行（argparse 的用法/报错最直观）
    /// → stdout 的 JSON 错误回执（业务错误的唯一出口）→ stdout 首行 → 超时/启动失败/退出码。
    /// </summary>
    public static string FailureText(ProcessResult result)
    {
        var stderr = FirstLine(result.StdErr);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            return stderr;
        }

        var receipt = ErrorFromReceipt(result.StdOut);
        if (receipt is not null)
        {
            return receipt;
        }

        // 回执是 JSON 但没有 error 字段（doctor 这类「体检报告」以退出码表达结论）：
        // 绝不能把首行那个「{」当成原因显示给用户。
        if (result.StdOut.TrimStart().StartsWith('{'))
        {
            var state = ReceiptText(result.StdOut, "status");
            return state is null
                ? $"官方 CLI 以退出码 {result.ExitCode} 结束，回执未给出 error 字段，详见完整输出"
                : $"官方 CLI 以退出码 {result.ExitCode} 结束，回执状态：{state}";
        }

        var stdout = FirstLine(result.StdOut);
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            return stdout;
        }

        if (result.TimedOut)
        {
            return "执行超时（已强制结束进程树）";
        }
        if (result.StartFailed)
        {
            return result.StartError ?? "无法启动官方 CLI 进程";
        }
        return $"官方 CLI 以退出码 {result.ExitCode} 结束，且没有输出";
    }

    /// <summary>把官方 CLI 的 JSON 错误回执读成人话；不是错误回执则返回 null。</summary>
    private static string? ErrorFromReceipt(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout) || !stdout.TrimStart().StartsWith('{'))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var message = Str(doc.RootElement, "error") ?? Str(doc.RootElement, "detail");
            var code = Str(doc.RootElement, "code");
            var field = Str(doc.RootElement, "field");
            if (message is null && code is null)
            {
                // package-upgrade 的 blocked 回执没有 error 字段，只有
                // state=blocked + error_type + reason（+ next_action）—— 翻译它而不是说「未给出」。
                if (Str(doc.RootElement, "state") is { } state && state == "blocked")
                {
                    var reason = Str(doc.RootElement, "reason");
                    var parts = new List<string> { "升级被阻止" };
                    if (reason is not null)
                    {
                        parts.Add(ReasonZh(reason));
                    }
                    else if (Str(doc.RootElement, "error_type") is { } etype)
                    {
                        parts.Add($"错误类型 {etype}");
                    }
                    if (Str(doc.RootElement, "next_action") is { } next)
                    {
                        parts.Add(NextActionZh(next));
                    }
                    return string.Join("：", parts.Where(p => !string.IsNullOrWhiteSpace(p))) + "。";
                }
                return null;
            }
            var text = message ?? "官方 CLI 报错（未给出原因）";
            if (code is not null)
            {
                // 注：插值串里不再嵌套引号字面量 —— verify_static.py V5 的字符串剥离是朴素正则，
                // 嵌套引号会把后面的花括号一起吞掉，导致误报括号不配平。
                var suffix = field is null ? "" : " · " + field;
                text += $"（{code}{suffix}）";
            }
            return text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>package-upgrade 阻止原因 → 人话（上游 package_upgrade.py 的固定 code）。</summary>
    private static string ReasonZh(string reason) => reason switch
    {
        "installed_files_locked_or_not_replaceable" =>
            "安装文件被占用（常见为后台 worker 或杀毒软件仍在使用），等这些进程退出后再执行一次即可",
        "stop_all_target_writers_and_restarters_first" => "需先停止所有写入者与自动重启器",
        "uv_required_no_pip_bootstrap" => "缺少外部 uv 可执行文件",
        "uv_must_be_external_executable" => "uv 路径无效（不存在或不是文件）",
        "uv_must_be_outside_target_venv" => "uv 不能位于目标 venv 内",
        "backup_and_wheel_must_be_outside_target_venv" => "备份目录与 wheel 不能放在目标 venv 内",
        "wrong_wheel_distribution" => "wheel 的发行版名称不对",
        "wheel_distribution_ambiguous" => "wheel 内含多个发行版，无法辨认",
        "record_outside_target_venv_or_missing" => "已装文件清单异常（缺失或不在 venv 内）",
        "backup_verification_failed" => "备份校验失败",
        "uv_install_failed" => "uv 换包失败",
        "installed_version_mismatch" => "换包后版本不符",
        "installed_import_failed" => "换包后无法导入包",
        "package_step_failed_backup_retained" => "换包失败（备份已保留）",
        _ => reason,
    };

    /// <summary>上游 next_action → 人话。</summary>
    private static string NextActionZh(string next) => next switch
    {
        "nothing_changed_wait_for_package_processes_to_exit_then_run_again" =>
            "本次什么都没动，等占用进程退出后再执行一次",
        "preserve_backup_keep_hosts_stopped_diagnose" => "请保留备份、保持各助手停止，先排查原因",
        "apply_install_then_doctor_before_restart" => "下一步：重跑安装应用与 doctor 再重启各助手",
        _ => "下一步：" + next,
    };

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = text.Split('\n')[0].Trim();
        return line.Length == 0 ? null : line;
    }

    /// <summary>
    /// 参数值加引号。Windows 命令行里「末尾反斜杠」会转义后面的引号，需成对加倍，
    /// 否则以反斜杠结尾的路径参数会把引号吃掉，并与后面的开关粘连成一个参数。
    /// </summary>
    private static string Quoted(string value)
    {
        var escaped = value.Replace("\"", "\\\"");
        if (escaped.EndsWith('\\'))
        {
            escaped += "\\";
        }
        return $"\"{escaped}\"";
    }
}
