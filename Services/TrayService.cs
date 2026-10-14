namespace ScopeRecallWin.Services;

/// <summary>
/// 托盘常驻。P4 实现：关闭窗口最小化到托盘（而非退出）。
/// WinUI 3 方案参考：CommunityToolkit.WinUI 的 TrayIcon，或基于
/// Shell_NotifyIcon P/Invoke 的自实现。右键菜单：打开控制台 / 立即巡检 / 退出（退出二次确认）。
/// 此骨架只保留挂载点说明，不在 P0 阶段引入第三方依赖。
/// </summary>
public static class TrayService
{
    // P4：注册 AppUserModelID、ShowTrayIcon()、HideTrayIcon()、OnTrayClick(恢复窗口)。
}
