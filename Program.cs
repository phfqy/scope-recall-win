using System;
using System.IO;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace ScopeRecallWin;

/// <summary>
/// 显式入口（unpackaged 形态）。
/// 不用 WinUI 自动生成的 Main，而是自己初始化 Windows App SDK Bootstrap：
/// 自动初始化（WindowsAppSdkBootstrapInitialize）在本机 self-contained 发布下会以
/// CoreMessagingXP.dll 触发 fail-fast（0xC0000602，App 构造函数都进不去）；
/// 显式 Bootstrap.TryInitialize + Application.Start 的组合已实测可用。
/// </summary>
internal static class Program
{
    /// <summary>Windows App SDK 1.8 的版本编码（major=1, minor=8）。</summary>
    private const uint BootstrapVersion = 0x0001_0008;

    [STAThread]
    private static void Main(string[] args)
    {
        var traceEnabled = Environment.GetEnvironmentVariable("SR_TRACE") == "1";
        var trace = Path.Combine(AppContext.BaseDirectory, "startup_trace.log");
        void Log(string line)
        {
            if (!traceEnabled)
            {
                return;
            }
            try
            {
                File.AppendAllText(trace, $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}\n");
            }
            catch
            {
                // 日志不可写时忽略
            }
        }

        if (traceEnabled)
        {
            try
            {
                File.Delete(trace);
            }
            catch
            {
                // 首次运行无文件
            }
        }

        try
        {
            var ok = Bootstrap.TryInitialize(BootstrapVersion, out var actual);
            Log($"bootstrap={ok} actual=0x{actual:X8}");
        }
        catch (Exception ex)
        {
            Log("bootstrap EX: " + ex.GetType().Name + ": " + ex.Message);
        }

        Log("stage=starting-xaml");
        try
        {
            Microsoft.UI.Xaml.Application.Start(_ => new App());
            Log("stage=xaml-returned");
        }
        catch (Exception ex)
        {
            Log("XAML EX: " + ex);
            throw;
        }
    }
}
