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
