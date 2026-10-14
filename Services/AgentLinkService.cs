using System.Diagnostics;
using System.Text.Json;
using ScopeRecallWin.Infrastructure;

namespace ScopeRecallWin.Services;

/// <summary>单个 Agent entry 的链接状态。</summary>
public sealed record AgentLinkStatus(
    string Entry,
    string Host,
    string Home,
    bool ConfigLinked,
    string ConfigDetail,
    bool ResidentAlive,
    int? Port,
    int? Pid,
    string? ResidentVersion,
    string? LastSeen,
    string Health,
    string HealthDetail);

/// <summary>
/// Agent 链接状态获取：三维度校验
///   1) 配置链接：宿主配置里是否注册了 scope-recall（mcp.json / hooks / provider / cordis.patch.yml）；
///   2) resident 常驻：端点注册表（%LOCALAPPDATA%\scope-recall\hook-endpoints\&lt;sha16(home)&gt;\&lt;pid&gt;.json）
///      中登记的进程是否仍在运行（端口 / pid / 版本）；
///   3) 最后活跃：entries.last_seen（库内）与安装清单 last_seen。
/// 全部只读，不建立协议级连接（HMAC 探活留待写入通道期）。
/// </summary>
public interface IAgentLinkService
{
    Task<IReadOnlyList<AgentLinkStatus>> ProbeAsync(CancellationToken ct = default);
}

public sealed class AgentLinkService : IAgentLinkService
{
    private readonly IPluginService? _plugin;

    public AgentLinkService(IPluginService? plugin)
    {
        _plugin = plugin;
    }

    public async Task<IReadOnlyList<AgentLinkStatus>> ProbeAsync(CancellationToken ct = default)
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null || _plugin is null)
        {
            return Array.Empty<AgentLinkStatus>();
        }

        var entries = await _plugin.ListEntriesAsync(root, ct).ConfigureAwait(false);
        var residents = ReadResidentEndpoints();
        var lastSeen = await ReadEntriesLastSeenAsync(root, ct).ConfigureAwait(false);

        var result = new List<AgentLinkStatus>();
        foreach (var entry in entries)
        {
            // A detached entry is no longer a link: the store keeps its memories, but the
            // panel must not report it as connected. detach() records detached_at and
            // leaves the row in installation.json, so filter on that, not on absence.
            if (!string.IsNullOrEmpty(entry.DetachedAt))
            {
                continue;
            }

            var (linked, detail) = CheckConfigLinked(entry);
            // 一个 host 可能有多个 resident 实例（如 workbuddy 双安装）
            var hosts = residents.Where(r => string.Equals(r.Host, entry.Host, StringComparison.OrdinalIgnoreCase)).ToList();
            var alive = hosts.Where(h => IsProcessAlive(h.Pid)).ToList();
            var best = alive.FirstOrDefault() ?? hosts.FirstOrDefault();

            // Hermes attaches through the memory provider (in-process recall), not the
            // MCP/resident channel: `scope-recall resident --host` accepts only
            // codex / claude-code / workbuddy / dsh, so a hermes entry never has a
            // resident and waiting for one would say "partly available" forever.
            var residentCapable = entry.Host is "codex" or "claude-code" or "workbuddy" or "dsh";

            var health = linked && (!residentCapable || alive.Count > 0) ? "ok"
                : linked ? "warn"
                : "danger";
            var healthDetail = health switch
            {
                "ok" when !residentCapable => "配置已注册（memory provider 通道，无需 resident）",
                "ok" => "配置已注册且 resident 在运行",
                "warn" => hosts.Count == 0 ? "配置已注册，resident 未启动（首次召回时惰性启动）" : "配置已注册，resident 进程已退出",
                _ => "宿主配置中未检测到 scope-recall 注册",
            };

            result.Add(new AgentLinkStatus(
                entry.Entry, entry.Host, entry.Home,
                linked, detail,
                alive.Count > 0,
                best?.Port, best?.Pid, best?.Version,
                lastSeen.TryGetValue(entry.Entry, out var seen) ? seen : null,
                health, healthDetail));
        }
        return result;
    }

    // ---------- resident 端点注册表 ----------

    private sealed record ResidentEndpoint(string Host, int Port, int Pid, string? Version, string File);

    private static List<ResidentEndpoint> ReadResidentEndpoints()
    {
        var list = new List<ResidentEndpoint>();
        try
        {
            var dir = PathDiscovery.HookEndpointsDir;
            if (!Directory.Exists(dir))
            {
                return list;
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    list.Add(new ResidentEndpoint(
                        root.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "",
                        root.TryGetProperty("port", out var p) ? p.GetInt32() : 0,
                        root.TryGetProperty("pid", out var pid) ? pid.GetInt32() : 0,
                        root.TryGetProperty("version", out var v) ? v.GetString() : null,
                        file));
                }
                catch
                {
                    // 单个端点文件损坏时跳过
                }
            }
        }
        catch
        {
            // 注册表不可读时返回空
        }
        return list;
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 宿主配置链接检查 ----------

    private static (bool Linked, string Detail) CheckConfigLinked(PluginEntry entry)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<(string Path, string Label)>();

        switch (entry.Host.ToLowerInvariant())
        {
            case "workbuddy":
                candidates.Add((Path.Combine(home, ".workbuddy", "mcp.json"), "mcp.json"));
                candidates.Add((Path.Combine(home, ".workbuddy", "settings.json"), "settings.json hooks"));
                candidates.Add((Path.Combine(home, ".workbuddy-ai", "mcp.json"), "mcp.json(ai)"));
                break;
            case "dsh":
                candidates.Add((Path.Combine(home, ".dsh", "cordis.patch.yml"), "cordis.patch.yml"));
                candidates.Add((Path.Combine(home, ".dsh", "scope-recall", "dsh-plugin", "index.mjs"), "dsh-plugin"));
                break;
            case "hermes":
                candidates.Add((Path.Combine(entry.Home, "config.yaml"), "config.yaml"));
                candidates.Add((Path.Combine(entry.Home, "plugins", "scope-recall", "plugin.yaml"), "plugin.yaml"));
                break;
            case "codex":
                candidates.Add((Path.Combine(home, ".codex", "config.toml"), "config.toml"));
                break;
            case "claude-code":
                candidates.Add((Path.Combine(home, ".claude", "settings.json"), "settings.json"));
                break;
        }

        var hits = new List<string>();
        var existing = new List<string>();
        foreach (var (path, label) in candidates)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                existing.Add(label);
                var text = File.ReadAllText(path);
                if (text.Contains("scope-recall", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("scope_recall", StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(label);
                }
            }
            catch
            {
                // 配置不可读时忽略该项
            }
        }

        if (hits.Count > 0)
        {
            return (true, $"已注册：{string.Join(" / ", hits)}");
        }
        if (existing.Count > 0)
        {
            return (false, $"配置文件存在但未检测到 scope-recall：{string.Join(" / ", existing)}");
        }
        return (false, "未找到宿主配置文件");
    }

    // ---------- 库内 entries.last_seen ----------

    private async Task<Dictionary<string, string>> ReadEntriesLastSeenAsync(string root, CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var storage = ServiceLocator.Get<IStorageService>();
        if (storage is null)
        {
            return dict;
        }
        var db = PathDiscovery.MemoryDbPath(root);
        if (!File.Exists(db))
        {
            return dict;
        }
        try
        {
            var rows = await storage.QueryAsync(db, "SELECT entry_id, last_seen FROM entries").ConfigureAwait(false);
            foreach (var row in rows)
            {
                var id = row.TryGetValue("entry_id", out var v) ? v?.ToString() : null;
                var seen = row.TryGetValue("last_seen", out var s) ? s?.ToString() : null;
                if (id is not null && seen is not null)
                {
                    dict[id] = seen;
                }
            }
        }
        catch
        {
            // 查询失败时忽略
        }
        return dict;
    }
}
