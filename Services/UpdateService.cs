using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScopeRecallWin.Services;

/// <summary>
/// 控制台软件自身的版本检查与升级（unpackaged exe 安装版）。
/// 检查：GitHub releases/latest API（直连失败自动切 gh-proxy 镜像）；
/// 升级：下载 Setup.exe → SHA256 校验 → 静默安装替换（/VERYSILENT）→ 退出当前进程。
/// 任何步骤不触碰记忆库与用户数据目录。
/// </summary>
public interface IUpdateService
{
    /// <summary>当前安装版本（程序集版本）。</summary>
    string CurrentVersion { get; }

    /// <summary>检查 GitHub 最新 Release。</summary>
    Task<UpdateCheckResult> CheckAsync(string repositoryOwner, string repositoryName, CancellationToken ct = default);

    /// <summary>下载安装包并校验 SHA256；返回本地临时文件路径。</summary>
    Task<UpdateDownloadResult> DownloadAsync(
        string downloadUrl, string? expectedSha256, IProgress<double>? progress, CancellationToken ct = default);

    /// <summary>启动静默安装（安装器负责关闭旧进程并替换文件）。返回是否成功拉起安装器。</summary>
    bool StartSilentInstall(string setupPath);
}

public sealed record UpdateCheckResult(
    string LatestVersion,
    string? Changelog,
    string? ReleaseUrl,
    string? SetupUrl,
    long? SetupSize,
    string? Sha256Sum,
    string Channel,
    DateTimeOffset CheckedAt,
    string? Error)
{
    public bool IsNewer { get; init; }
}

public sealed record UpdateDownloadResult(string? FilePath, string? Error, string? ActualSha256);

public sealed class UpdateService : IUpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>GitHub API 直连。</summary>
    private const string GitHubApi = "https://api.github.com";

    /// <summary>gh-proxy 镜像前缀（本机实测 GitHub 直连不稳定时可用）。</summary>
    private const string MirrorApi = "https://gh-proxy.com/https://api.github.com";

    private const string MirrorDownload = "https://gh-proxy.com/https://github.com";

    public string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string repositoryOwner, string repositoryName, CancellationToken ct = default)
    {
        var apiPath = $"/repos/{repositoryOwner}/{repositoryName}/releases/latest";
        var attempts = new (string Base, string Channel)[]
        {
            (GitHubApi, "GitHub 直连"),
            (MirrorApi, "gh-proxy 镜像"),
        };

        string? lastError = null;
        foreach (var (baseUrl, channel) in attempts)
        {
            try
            {
                var url = baseUrl + apiPath;
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("ScopeRecallWin/1.0");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"{channel} HTTP {(int)resp.StatusCode}";
                    continue;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var version = tag.TrimStart('v', 'V');
                var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

                // 找 Setup.exe 资产
                string? setupUrl = null;
                long? setupSize = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.StartsWith("ScopeRecallWin-Setup-", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            setupUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                            setupSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : null;
                            break;
                        }
                    }
                }

                // 下载 URL 改写为镜像（直连 objects.githubusercontent.com 不稳定）
                var downloadUrl = setupUrl;
                if (!string.IsNullOrEmpty(setupUrl) && channel.Contains("镜像"))
                {
                    downloadUrl = MirrorDownload + setupUrl[GitHubApi.Length..];
                }
                else if (!string.IsNullOrEmpty(setupUrl))
                {
                    downloadUrl = MirrorDownload + setupUrl["https://github.com".Length..];
                }

                var sha = ParseSha256FromNotes(body);
                var result = new UpdateCheckResult(
                    version, body, htmlUrl, downloadUrl, setupSize, sha, channel, DateTimeOffset.Now, null);
                return result with { IsNewer = IsNewerThan(version, CurrentVersion) };
            }
            catch (Exception ex)
            {
                lastError = $"{channel}: {ex.Message}";
            }
        }

        return new UpdateCheckResult("", null, null, null, null, null, "失败", DateTimeOffset.Now,
            lastError ?? "所有通道均失败");
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        string downloadUrl, string? expectedSha256, IProgress<double>? progress, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.UserAgent.ParseAdd("ScopeRecallWin/1.0");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1L;
            var target = Path.Combine(Path.GetTempPath(), $"ScopeRecallWin-Setup-{Guid.NewGuid():N}.exe");

            long received = 0;
            await using (var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(target))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                    {
                        progress?.Report((double)received / total);
                    }
                }
            }

            progress?.Report(1.0);
            var actualSha = ComputeSha256(target);
            if (!string.IsNullOrEmpty(expectedSha256)
                && !string.Equals(actualSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(target); } catch { }
                return new UpdateDownloadResult(null,
                    $"SHA256 校验失败（期望 {expectedSha256}，实际 {actualSha}）", actualSha);
            }
            return new UpdateDownloadResult(target, null, actualSha);
        }
        catch (Exception ex)
        {
            return new UpdateDownloadResult(null, ex.Message, null);
        }
    }

    public bool StartSilentInstall(string setupPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 内部 ----------

    /// <summary>从 Release 说明的 SHA256 代码块提取 64 位十六进制。</summary>
    internal static string? ParseSha256FromNotes(string? notes)
    {
        if (string.IsNullOrEmpty(notes))
        {
            return null;
        }
        var m = Regex.Match(notes, @"\b([0-9a-fA-F]{64})\b");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    internal static bool IsNewerThan(string candidate, string current)
    {
        if (Version.TryParse(candidate, out var c) && Version.TryParse(current, out var n))
        {
            return c > n;
        }
        return false;
    }

    internal static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
