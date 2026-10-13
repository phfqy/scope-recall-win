using System.Net.Http;
using System.Text.Json;

namespace ScopeRecallWin.Services;

/// <summary>插件版本检查与一键更新。P4 实现。</summary>
public interface IUpdateService
{
    Task<UpdateCheckResult?> CheckAsync(string repositoryOwner, string repositoryName);
}

public sealed record UpdateCheckResult(
    string LatestVersion, string? ChangelogUrl, string? DownloadUrl, string? Sha256Sum,
    string Channel, DateTimeOffset CheckedAt, string? Error);

/// <summary>
/// 更新骨架。P4 落实：
///   - HttpClient 读 GitHub release（直连失败自动切镜像源，可配置）；
///   - 全流程：预检 → 备份现版本（<APP>\backups\<版本>-<时间戳>）→ 下载 → sha256 校验 →
///     停 worker → 替换 <CONSOLE> 代码 → 依赖重装 → 起 worker → 自检（doctor exit 0/schema 不变）→
///     失败自动回滚；日志 <APP>\logs\update.log（约束 9）。
/// 任何步骤不碰 <STORE> 与用户配置目录。
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private static readonly HttpClient Http = new();

    public async Task<UpdateCheckResult?> CheckAsync(string repositoryOwner, string repositoryName)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repositoryOwner}/{repositoryName}/releases/latest";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("ScopeRecallWin/1.0");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult("", null, null, null, $"GitHub {resp.StatusCode}", DateTimeOffset.Now, null);
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return new UpdateCheckResult(
                root.TryGetProperty("tag_name", out var v) ? v.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                null, null, "GitHub 直连", DateTimeOffset.Now, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult("", null, null, null, "失败", DateTimeOffset.Now, ex.Message);
        }
    }
}
