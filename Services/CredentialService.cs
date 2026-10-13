namespace ScopeRecallWin.Services;

/// <summary>AI 凭据存取。约束 7：API Key 只存 Windows Credential Manager，password 输入、不回显、不落日志。</summary>
public interface ICredentialService
{
    Task<string?> GetAsync(string serviceName, string accountName);
    Task<bool> SaveAsync(string serviceName, string accountName, string secret);
    Task<bool> DeleteAsync(string serviceName, string accountName);
}

/// <summary>
/// 凭据骨架。P3 实现：Windows Credential Manager（CredWrite/CredRead P/Invoke，或 Windows.Security.
/// Credentials.PasswordVault）。
/// 设计约束：
///   - 值绝不出现在 runtime-config.json / 日志 / 调用台账；
///   - 配置文件只保留 credential_env 的"环境变量名"，key 值由凭据管理器提供并注入进程环境。
/// </summary>
public sealed class CredentialService : ICredentialService
{
    public Task<string?> GetAsync(string serviceName, string accountName)
        => Task.FromResult<string?>(null); // 骨架占位

    public Task<bool> SaveAsync(string serviceName, string accountName, string secret)
        => Task.FromResult(false); // 骨架占位

    public Task<bool> DeleteAsync(string serviceName, string accountName)
        => Task.FromResult(false); // 骨架占位
}
