using Microsoft.UI.Xaml;

namespace ScopeRecallWin;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 启动窗口。托盘常驻 / 开机自启 / 单实例：在 P4 由 Services/TrayService、StartupService 接入。
        _window = new MainWindow();
        _window.Activate();
    }
}
