using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class DoctorView : UserControl
{
    /// <summary>诊断结果行（具名字段便于复制/保存时取用）。</summary>
    private sealed record DoctorRow(string Title, string Status, string Meta, string Report);

    private readonly IPluginService? _plugin;
    private readonly List<DoctorRow> _rows = new();
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
                TxtSummary.Text = "未找到记忆库：请先到「安装」页选择部署目录。";
                return;
            }
            var venvPython = PathDiscovery.FindVenvPython(root);
            if (venvPython is null)
            {
                TxtSummary.Text = "未找到共享运行环境（shared-venv），无法运行诊断。";
                return;
            }

            _running = true;
            BtnRun.IsEnabled = false;
            BtnCopyAll.IsEnabled = false;
            BtnSaveReport.IsEnabled = false;
            TxtSummary.Text = "正在逐个助手运行体检，请稍候…";
            EntryList.ItemsSource = null;
            _rows.Clear();

            var entries = _plugin is null
                ? new List<PluginEntry>()
                : (await _plugin.ListEntriesAsync(root)).ToList();

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

                var report = output.Length > 4000 ? output[..4000] + "\n…（已截断）" : output;
                _rows.Add(new DoctorRow(
                    Title: string.IsNullOrWhiteSpace(entry.DisplayName)
                        ? entry.Entry
                        : $"{entry.DisplayName}（{entry.Entry}）",
                    Status: status,
                    Meta: $"类型 {entry.Host} · {StatusVisuals.StateLabel(status)} · 耗时 {r.Duration.TotalMilliseconds:F0} 毫秒",
                    Report: $"状态：{StatusVisuals.StateLabel(status)}（原始值 {status}）\n{report}"));
            }

            EntryList.ItemsSource = _rows;
            BtnCopyAll.IsEnabled = _rows.Count > 0;
            BtnSaveReport.IsEnabled = _rows.Count > 0;
            TxtSummary.Text = entries.Count == 0
                ? "还没有接入任何助手（共享库中没有成员），暂时无法运行诊断。"
                : $"诊断完成：共 {entries.Count} 个助手，正常 {okCount} 个、需注意 {entries.Count - okCount} 个。可逐份查看或导出报告。";
            _running = false;
            BtnRun.IsEnabled = true;
        });
    }

    // ---------- 报告复制 / 保存 ----------

    private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DoctorRow row })
        {
            return;
        }
        CopyToClipboard($"{row.Title}\n{row.Meta}\n\n{row.Report}");
        TxtSummary.Text = $"已复制「{row.Title}」的诊断报告到剪贴板。";
    }

    private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            return;
        }
        CopyToClipboard(BuildCombinedReport());
        TxtSummary.Text = $"已复制全部 {_rows.Count} 份报告到剪贴板。";
    }

    private void BtnSaveReport_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "DoctorView.BtnSaveReport_Click", async () =>
        {
            if (_rows.Count == 0)
            {
                return;
            }
            await Exporter.SaveWithPickerAsync(
                $"诊断报告-{DateTimeOffset.Now:yyyyMMdd-HHmm}.md",
                BuildCombinedReport(),
                ".md",
                "Markdown");
        });

    private string BuildCombinedReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Scope Recall 记忆工作台 · 诊断报告（{DateTimeOffset.Now:yyyy-MM-dd HH:mm}）");
        sb.AppendLine();
        foreach (var row in _rows)
        {
            sb.AppendLine($"=== {row.Title} ===");
            sb.AppendLine(row.Meta);
            sb.AppendLine(row.Report);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            App.LogCrash("DoctorView.CopyToClipboard", ex);
        }
    }
}
