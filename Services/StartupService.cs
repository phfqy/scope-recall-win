namespace ScopeRecallWin.Services;

/// <summary>
/// 开机自启。P4 实现（回答"重启后谁启动"）：
///   - packaged（MSIX）：Package.appxmanifest 已声明 StartupTask "ScopeRecallWinStartup"
///     （Enabled=false，默认不自启）；运行时通过
///     Windows.ApplicationModel.StartupTask.GetForCurrentPackageAsync().RequestEnableAsync()
///     开启 / Disable() 关闭；
///   - 非 packaged 备选：注册表 Run key（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
/// 默认关闭，用户显式开启（约束 9）。
/// </summary>
public static class StartupService
{
    // P4：EnableAsync() / DisableAsync() / IsEnabledAsync()。
}
