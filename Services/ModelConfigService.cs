using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScopeRecallWin.Infrastructure;

namespace ScopeRecallWin.Services;

/// <summary>记忆所用大模型配置（runtime-config.json 的 auxiliary / vector 段）。</summary>
public sealed record ModelConfig(
    string EmbeddingModel,
    string EmbeddingEndpoint,
    string EmbeddingCredentialEnv,
    int EmbeddingDimensions,
    string ConsolidationModel,
    string ConsolidationEndpoint,
    string ConsolidationKind,
    int? MaxOutputTokens,
    bool ExternalEmbedding,
    bool ExternalConsolidation,
    double VectorThreshold,
    string? ConfigPath);

public sealed record ModelSaveResult(bool Ok, string? Error, string? BackupPath);

public sealed record ConnectionTestResult(bool Ok, int LatencyMs, string Detail, bool NeedsKey);

/// <summary>
/// 云端记忆大模型配置：读取 / 备份写入 runtime-config.json（保序、只改指定字段），
/// API Key 走 Windows 凭据管理器（不落配置文件），并提供连通性测试（只验连接、不做推理）。
/// </summary>
public interface IModelConfigService
{
    Task<ModelConfig?> LoadAsync(CancellationToken ct = default);
    Task<ModelSaveResult> SaveAsync(ModelConfig config, CancellationToken ct = default);
    Task<ConnectionTestResult> TestConnectionAsync(string endpoint, string credentialEnv, CancellationToken ct = default);
    Task<bool> HasStoredKeyAsync(string credentialEnv);
    Task<bool> StoreKeyAsync(string credentialEnv, string secret);
    Task<bool> DeleteKeyAsync(string credentialEnv);
}

public sealed class ModelConfigService : IModelConfigService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public Task<ModelConfig?> LoadAsync(CancellationToken ct = default)
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null)
        {
            return Task.FromResult<ModelConfig?>(null);
        }
        var path = PathDiscovery.RuntimeConfigPath(root);
        if (!File.Exists(path))
        {
            return Task.FromResult<ModelConfig?>(null);
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is null)
            {
                return Task.FromResult<ModelConfig?>(null);
            }
            var auxiliary = node["auxiliary"];
            var embedding = auxiliary?["embedding"];
            var consolidation = auxiliary?["consolidation"];
            var vector = node["vector"];

            var config = new ModelConfig(
                Str(embedding?["model"]),
                Str(embedding?["endpoint"]),
                Str(embedding?["credential_env"]),
                Int(embedding?["dimensions"], 2048),
                Str(consolidation?["model"]),
                Str(consolidation?["endpoint"]),
                Str(consolidation?["kind"]),
                IntOrNull(consolidation?["max_output_tokens"]),
                Bool(auxiliary?["external_embedding"], true),
                Bool(auxiliary?["external_consolidation"], true),
                Double(node["vector_threshold"], 0.7),
                path);
            return Task.FromResult<ModelConfig?>(config);
        }
        catch
        {
            return Task.FromResult<ModelConfig?>(null);
        }
    }

    public async Task<ModelSaveResult> SaveAsync(ModelConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.ConfigPath) || !File.Exists(config.ConfigPath))
        {
            return new ModelSaveResult(false, "未找到 runtime-config.json。", null);
        }

        try
        {
            // 1) 备份原配置
            var root = PathDiscovery.FindMemoryRoot(App.Settings);
            string? backupPath = null;
            if (root is not null)
            {
                var dir = Path.Combine(root, PluginService.WorkDirName);
                Directory.CreateDirectory(dir);
                backupPath = Path.Combine(dir, $"runtime-config-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(config.ConfigPath, backupPath, overwrite: false);
            }

            // 2) 保序更新指定字段（JsonNode 保持原有键顺序与其他字段）
            var node = JsonNode.Parse(File.ReadAllText(config.ConfigPath)!)!;
            var auxiliary = node["auxiliary"] as JsonObject ?? new JsonObject();
            if (node["auxiliary"] is null)
            {
                node["auxiliary"] = auxiliary;
            }
            var embedding = auxiliary["embedding"] as JsonObject ?? new JsonObject();
            if (auxiliary["embedding"] is null)
            {
                auxiliary["embedding"] = embedding;
            }
            var consolidation = auxiliary["consolidation"] as JsonObject ?? new JsonObject();
            if (auxiliary["consolidation"] is null)
            {
                auxiliary["consolidation"] = consolidation;
            }

            embedding["model"] = config.EmbeddingModel;
            embedding["endpoint"] = config.EmbeddingEndpoint;
            embedding["credential_env"] = config.EmbeddingCredentialEnv;
            embedding["dimensions"] = config.EmbeddingDimensions;
            consolidation["model"] = config.ConsolidationModel;
            consolidation["endpoint"] = config.ConsolidationEndpoint;
            consolidation["kind"] = config.ConsolidationKind;
            if (config.MaxOutputTokens is { } maxTokens)
            {
                consolidation["max_output_tokens"] = maxTokens;
            }
            auxiliary["external_embedding"] = config.ExternalEmbedding;
            auxiliary["external_consolidation"] = config.ExternalConsolidation;
            node["vector_threshold"] = config.VectorThreshold;

            // 3) 原子写（先写临时文件再替换，避免写坏配置）
            var temp = config.ConfigPath + ".tmp";
            File.WriteAllText(temp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.Replace(temp, config.ConfigPath, null, ignoreMetadataErrors: true);

            await Task.CompletedTask.ConfigureAwait(false);
            return new ModelSaveResult(true, null, backupPath);
        }
        catch (Exception ex)
        {
            return new ModelSaveResult(false, ex.Message, null);
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        string endpoint, string credentialEnv, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ConnectionTestResult(false, 0, "endpoint 为空", false);
        }

        var baseUrl = DeriveBaseUrl(endpoint);
        var key = ServiceLocator.Get<ICredentialService>() is { } credentials
            ? await credentials.GetAsync(credentialEnv, "apikey")
            : null;
        var needsKey = string.IsNullOrEmpty(key);

        var url = baseUrl.TrimEnd('/') + "/models";
        var started = DateTimeOffset.Now;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!needsKey)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            using var resp = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var ms = (int)(DateTimeOffset.Now - started).TotalMilliseconds;
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                return new ConnectionTestResult(true, ms, $"HTTP {code} 可达且鉴权通过", false);
            }
            if (code is 401 or 403)
            {
                return new ConnectionTestResult(false, ms, $"HTTP {code}：端点可达但鉴权失败（API Key 无效或未配置）", true);
            }
            return new ConnectionTestResult(false, ms, $"HTTP {code}（端点可达，但 /models 不可用；仅连通性判断）", needsKey);
        }
        catch (Exception ex)
        {
            var ms = (int)(DateTimeOffset.Now - started).TotalMilliseconds;
            return new ConnectionTestResult(false, ms, $"连接失败：{ex.Message}", needsKey);
        }
    }

    public async Task<bool> HasStoredKeyAsync(string credentialEnv)
    {
        if (ServiceLocator.Get<ICredentialService>() is not { } credentials || string.IsNullOrWhiteSpace(credentialEnv))
        {
            return false;
        }
        var key = await credentials.GetAsync(credentialEnv, "apikey");
        return !string.IsNullOrEmpty(key);
    }

    public Task<bool> StoreKeyAsync(string credentialEnv, string secret)
    {
        if (ServiceLocator.Get<ICredentialService>() is not { } credentials || string.IsNullOrWhiteSpace(credentialEnv))
        {
            return Task.FromResult(false);
        }
        return credentials.SaveAsync(credentialEnv, "apikey", secret);
    }

    public Task<bool> DeleteKeyAsync(string credentialEnv)
    {
        if (ServiceLocator.Get<ICredentialService>() is not { } credentials || string.IsNullOrWhiteSpace(credentialEnv))
        {
            return Task.FromResult(false);
        }
        return credentials.DeleteAsync(credentialEnv, "apikey");
    }

    // ---------- 工具 ----------

    /// <summary>从具体接口地址推导 API base（去掉 /chat/completions、/embeddings 等后缀）。</summary>
    internal static string DeriveBaseUrl(string endpoint)
    {
        var url = endpoint.Trim();
        foreach (var suffix in new[] { "/chat/completions", "/embeddings", "/completions" })
        {
            var index = url.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                return url[..index];
            }
        }
        return url;
    }

    private static string Str(JsonNode? node) => node?.GetValue<string>() ?? "";
    private static int Int(JsonNode? node, int fallback) => node is null ? fallback : (int.TryParse(node.ToJsonString(), out var v) ? v : fallback);
    private static int? IntOrNull(JsonNode? node) => node is null ? null : (int.TryParse(node.ToJsonString(), out var v) ? v : null);
    private static double Double(JsonNode? node, double fallback) => node is null ? fallback : (double.TryParse(node.ToJsonString(), out var v) ? v : fallback);
    private static bool Bool(JsonNode? node, bool fallback) => node is null ? fallback : (bool.TryParse(node.ToJsonString(), out var v) ? v : fallback);
}
