using System;
using Microsoft.UI.Xaml;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin;

public partial class App : Application
{
    private Window? _window;

    /// <summary>进程级设置实例（OnLaunched 时加载，运行期只读使用；保存走 Save()）。</summary>
    public static AppSettings Settings { get; } = AppSettings.Load();

    /// <summary>主窗口实例（FolderPicker 等需要窗口句柄的场景使用）。</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>启动跟踪日志（诊断 unpackaged fail-fast 用，写入 exe 同目录 startup_trace.log）。
    /// 仅在环境变量 SR_TRACE=1 时启用，避免正常运行期产生垃圾文件。</summary>
    internal static string TracePath => System.IO.Path.Combine(AppContext.BaseDirectory, "startup_trace.log");

    internal static bool TraceEnabled =>
        Environment.GetEnvironmentVariable("SR_TRACE") == "1";

    internal static void Trace(string message)
    {
        if (!TraceEnabled)
        {
            return;
        }
        try
        {
            System.IO.File.AppendAllText(
                TracePath,
                $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // 日志不可写时忽略
        }
    }

    public App()
    {
        if (TraceEnabled)
        {
            try
            {
                System.IO.File.Delete(TracePath);
            }
            catch
            {
                // 首次运行无文件
            }
        }
        Trace("App ctor: begin");
        InitializeComponent();
        Trace("App ctor: InitializeComponent done");

        // 未处理异常也记进追踪日志（fail-fast 之外的可捕获异常）
        UnhandledException += (_, e) =>
        {
            Trace("UNHANDLED: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace("OnLaunched: begin");
        RegisterServices();
        Trace("OnLaunched: services registered");

        try
        {
            _window = new MainWindow();
            MainWindow = _window as MainWindow;
            Trace("OnLaunched: MainWindow constructed");
            _window.Activate();
            Trace("OnLaunched: activated");
        }
        catch (Exception ex)
        {
            Trace("OnLaunched EXCEPTION: " + ex);
            throw;
        }
    }

    /// <summary>注册全部服务实现（无 DI 容器，静态定位器）。</summary>
    private static void RegisterServices()
    {
        ServiceLocator.Register<IUpdateService>(new UpdateService());
        ServiceLocator.Register<IStorageService>(new StorageService());
        ServiceLocator.Register<IMutateService>(new MutateService());
        ServiceLocator.Register<IWorkerService>(new WorkerService());
        ServiceLocator.Register<ICredentialService>(new CredentialService());

        var plugin = new PluginService();
        ServiceLocator.Register<IPluginService>(plugin);
        ServiceLocator.Register<IAgentLinkService>(new AgentLinkService(plugin));
        ServiceLocator.Register<IModelConfigService>(new ModelConfigService());
    }
}
