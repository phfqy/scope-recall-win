using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class DoctorView : UserControl
{
    private readonly IPluginService? _plugin;
    private bool _running;

    public DoctorView()
    {
        InitializeComponent();
        _plugin = ServiceLocator.Get<IPluginService>();
    }

    private void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "DoctorView.BtnRun_Click", async () =>
        {
            if (_running)
            {
                return;
            }
            var root = PathDiscovery.FindMemoryRoot(App.Settings);
            if (root is null)
            {
                TxtSummary.Text = "未找到记忆库根目录：请先在「安装」页选择部署目录。";
                return;
            }
            var venvPython = PathDiscovery.FindVenvPython(root);
            if (venvPython is null)
            {
                TxtSummary.Text = "未找到共享 venv（shared-venv\\Scripts\\python.exe）。";
                return;
            }

            _running = true;
            BtnRun.IsEnabled = false;
            TxtSummary.Text = "正在运行 doctor（逐 entry）…";
            EntryList.ItemsSource = null;

            var entries = _plugin is null
                ? new List<PluginEntry>()
                : (await _plugin.ListEntriesAsync(root)).ToList();

            var display = new List<object>();
            var okCount = 0;
            foreach (var entry in entries)
            {
                var instanceRoot = entry.Home is { Length: > 0 } ? entry.Home : PathDiscovery.SharedDir(root);
                var host = entry.Host is { Length: > 0 } ? entry.Host : "codex";
                var r = await ProcessRunner.RunAsync(
                    venvPython,
                    $"-m scope_recall.maintenance.cli doctor --host {host} --instance-root \"{instanceRoot}\" --python \"{venvPython}\"",
                    timeoutMs: 180_000);

                var output = (r.StdOut + "\n" + r.StdErr).Trim();
                var status = !r.Success ? "failed"
                    : output.Contains("degraded", StringComparison.OrdinalIgnoreCase) ? "degraded"
                    : "ok";
                if (status == "ok")
                {
                    okCount++;
                }

                var report = output.Length > 4000 ? output[..4000] + "\n…（截断）" : output;
                display.Add(new
                {
                    Title = $"{entry.Entry}（host {entry.Host}）",
                    Meta = $"host {entry.Host} · {status} · {r.Duration.TotalMilliseconds:F0}ms",
                    Report = $"status: {status}\n{report}",
                });
            }

            EntryList.ItemsSource = display;
            TxtSummary.Text = entries.Count == 0
                ? "installation.json 中没有 entry（未 attach 任何宿主）。"
                : $"诊断完成：{entries.Count} 个 entry，ok {okCount} / 异常 {entries.Count - okCount}。";
            _running = false;
            BtnRun.IsEnabled = true;

        });
    }
}
