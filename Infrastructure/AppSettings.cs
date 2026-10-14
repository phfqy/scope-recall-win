using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScopeRecallWin.Infrastructure;

/// <summary>
/// 用户级设置（%LOCALAPPDATA%\ScopeRecallWin\settings.json）。
/// 不含密钥：API Key 一律走 Windows 凭据管理器（Services/CredentialService）。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>记忆库根目录（含 shared/ 与 shared-venv/），部署时由用户选择。</summary>
    public string? MemoryRootPath { get; set; }

    /// <summary>控制台自身更新模式：auto=自动下载并静默替换；notify=仅提示手动下载。</summary>
    public string UpdateMode { get; set; } = "auto";

    /// <summary>更新源：github=直连；mirror=走镜像（gh-proxy）。</summary>
    public string UpdateChannel { get; set; } = "github";

    /// <summary>界面语言预留（当前仅简体中文）。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>外观主题：system=跟随系统；light=浅色；dark=深色。</summary>
    public string Theme { get; set; } = "system";

    // ---------- 云同步（记忆云同步桥）----------
    // 本组设置只放非敏感项。云端口令一律走 Windows 凭据管理器
    // （Services/CredentialService），绝不写入 settings.json、日志或命令行参数。

    /// <summary>云同步总开关。默认关闭：未显式开启前不启动桥、不触碰云端。</summary>
    public bool CloudSyncEnabled { get; set; }

    /// <summary>云端后端：local=本地目录（测试）；webdav=坚果云/AList 等；s3=OSS/COS/R2。</summary>
    public string CloudSyncBackend { get; set; } = "webdav";

    /// <summary>WebDAV 服务地址（如 https://dav.jianguoyun.com/dav/）。</summary>
    public string? CloudSyncUrl { get; set; }

    /// <summary>WebDAV 用户名（坚果云为账号邮箱）。</summary>
    public string? CloudSyncUser { get; set; }

    /// <summary>S3 bucket（backend=s3 时使用）。</summary>
    public string? CloudSyncBucket { get; set; }

    /// <summary>S3 endpoint（backend=s3 时使用）。</summary>
    public string? CloudSyncEndpoint { get; set; }

    /// <summary>S3 区域；兼容 S3 服务通常填 auto。</summary>
    public string CloudSyncRegion { get; set; } = "auto";

    /// <summary>云端根路径/前缀（云端目录下的子路径）。</summary>
    public string CloudSyncRoot { get; set; } = "";

    /// <summary>同步轮询间隔（秒）。坚果云免费版对高频请求返回 503，建议不低于 60 秒。</summary>
    public int CloudSyncIntervalSeconds { get; set; } = 60;

    /// <summary>是否上传原始事件正文。关闭则只同步 claims，隐私更干净。</summary>
    public bool CloudSyncIncludeEvents { get; set; } = true;

    /// <summary>桥与增量引擎使用的专用 Python 解释器（独立 venv，不污染插件 venv）。</summary>
    public string? CloudSyncPythonPath { get; set; }

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopeRecallWin");

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值，不阻塞启动
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 磁盘不可写时静默失败（设置不持久化，不影响本次运行）
        }
    }
}
