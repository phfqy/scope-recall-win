namespace ScopeRecallWin.Services;

/// <summary>
/// 开机自启。P4 实现（回答"重启后谁启动"）：
///   - 本工程为 unpackaged（WindowsPackageType=None）形态，StartupTask 不可用；
///     自启走 HKCU 注册表 Run key（HKCU\Software\Microsoft\Windows\CurrentVersion\Run），
///     值指向安装目录下的 ScopeRecallWin.exe（免管理员、per-user）；
///   - 备选：计划任务（登录时触发，支持延迟与条件）。
/// 默认关闭，用户显式开启（约束 9）；卸载时由安装器清理对应 Run 值。
/// </summary>
public static class StartupService
{
    // P4：EnableAsync() / DisableAsync() / IsEnabledAsync()。
}
