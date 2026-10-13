using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class SharedStoreView : UserControl
{
    private readonly IPluginService? _plugin;
    private bool _loaded;

    public SharedStoreView()
    {
        InitializeComponent();
        _plugin = ServiceLocator.Get<IPluginService>();
        Loaded += SharedStoreView_Loaded;
    }

    private void SharedStoreView_Loaded(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "SharedStoreView.SharedStoreView_Loaded", async () =>
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            await ReloadAsync();

        });
    }

    private async Task ReloadAsync()
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        TxtRoot.Text = root ?? "未找到共享库（请先在「安装」页选择部署目录）";
        if (root is null || _plugin is null)
        {
            TxtState.Text = "";
            return;
        }

        var entries = (await _plugin.ListEntriesAsync(root)).ToList();
        EntryList.ItemsSource = entries.Select(en => new
        {
            Title = en.Entry,
            Meta = $"host {en.Host} · home {en.Home} · python {en.Python ?? "（宿主自带）"} · 目录存在 {(en.Alive ? "是" : "否")}",
        }).ToList();
        TxtState.Text = entries.Count == 0
            ? "当前共享库还没有 entry：可在下方 attach 宿主。"
            : $"共 {entries.Count} 个 entry。";
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "SharedStoreView.BtnRefresh_Click", ReloadAsync);

    // ---------- attach（写操作，CLI + 护栏） ----------

    private void BtnAttach_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "SharedStoreView.BtnAttach_Click", async () =>
        {
            var root = PathDiscovery.FindMemoryRoot(App.Settings);
            if (root is null)
            {
                TxtAttachState.Text = "未找到共享库根目录。";
                return;
            }
            var venvPython = PathDiscovery.FindVenvPython(root);
            if (venvPython is null)
            {
                TxtAttachState.Text = "未找到共享 venv。";
                return;
            }

            var host = ((ComboBoxItem)CmbHost.SelectedItem)?.Content?.ToString() ?? "";
            var entry = (TxtEntryId.Text ?? "").Trim();
            var displayName = (TxtDisplayName.Text ?? "").Trim();
            var instanceRoot = (TxtInstanceRoot.Text ?? "").Trim();
            var auth = (TxtAttachAuth.Text ?? "").Trim();

            if (!Regex.IsMatch(entry, "^[a-z0-9-]{2,32}$"))
            {
                TxtAttachState.Text = "entry 需 2..32 位小写字母/数字/连字符。";
                return;
            }
            if (displayName.Length is < 1 or > 32)
            {
                TxtAttachState.Text = "display_name 需 1..32 字符。";
                return;
            }
            if (instanceRoot.Length == 0 || !Directory.Exists(instanceRoot))
            {
                TxtAttachState.Text = "instance_root 无效或不存在。";
                return;
            }
            if (auth.Length < 6 || !auth.Contains(entry, StringComparison.Ordinal))
            {
                TxtAttachState.Text = "授权句需 ≥6 字且点明 entry（不代笔 / 不预填）。";
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "确认 attach",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"将执行：\nscope_recall.maintenance.cli attach --host {host} --instance-root \"{instanceRoot}\" " +
                           $"--root \"{root}\" --entry {entry} --display-name \"{displayName}\"\n\n" +
                           "该操作会把该宿主的原库并入共享库，请确认你本人了解本次操作。",
                },
                PrimaryButtonText = "确认执行",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            TxtAttachState.Text = "正在执行 attach…";
            var r = await ProcessRunner.RunAsync(
                venvPython,
                $"-m scope_recall.maintenance.cli attach --host {host} --instance-root \"{instanceRoot}\" " +
                $"--root \"{root}\" --entry {entry} --display-name \"{displayName}\"",
                timeoutMs: 600_000);

            TxtAttachState.Text = r.Success
                ? $"attach 完成（exit 0）。\n{FirstLines(r.StdOut, 6)}"
                : $"attach 失败（exit {r.ExitCode}）：{FirstLine(r.StdErr) ?? FirstLine(r.StdOut) ?? "未知错误"}";
            if (r.Success)
            {
                await ReloadAsync();
            }

        });
    }

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = text.Split('\n')[0].Trim();
        return line.Length == 0 ? null : line;
    }

    private static string FirstLines(string text, int count)
        => string.IsNullOrWhiteSpace(text) ? "" : string.Join("\n", text.Split('\n').Take(count)).Trim();
}
