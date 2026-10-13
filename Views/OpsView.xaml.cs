using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class OpsView : UserControl
{
    private readonly IPluginService? _plugin;

    public OpsView()
    {
        InitializeComponent();
        _plugin = ServiceLocator.Get<IPluginService>();
    }

    private (string Root, string VenvPython, string Db)? ResolveEnv()
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null)
        {
            return null;
        }
        var venv = PathDiscovery.FindVenvPython(root);
        if (venv is null)
        {
            return null;
        }
        var db = PathDiscovery.MemoryDbPath(root);
        return (root, venv, db);
    }

    // ---------- entries ----------

    private void BtnRefreshEntries_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "OpsView.BtnRefreshEntries_Click", async () =>
        {
            var root = PathDiscovery.FindMemoryRoot(App.Settings);
            if (root is null || _plugin is null)
            {
                TxtEntriesState.Text = "未找到记忆库根目录。";
                return;
            }
            var entries = (await _plugin.ListEntriesAsync(root)).ToList();
            EntryList.ItemsSource = entries.Select(en => new
            {
                Title = $"{en.Entry} — host {en.Host}",
                Meta = $"host {en.Host} · home {en.Home} · 目录存在 {(en.Alive ? "是" : "否")}",
            }).ToList();
            TxtEntriesState.Text = $"共 {entries.Count} 个 entry。";

        });
    }

    // ---------- 备份 ----------

    private void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "OpsView.BtnBackup_Click", async () =>
        {
            var env = ResolveEnv();
            if (env is null)
            {
                TxtBackupState.Text = "未找到记忆库根目录或共享 venv。";
                return;
            }
            if (!File.Exists(env.Value.Db))
            {
                TxtBackupState.Text = "未找到 memory.sqlite3。";
                return;
            }

            var (confirmed, _) = await ConfirmAsync(
                "备份记忆库",
                "将调用官方 backup CLI 生成一致性快照（quick_check + sha256 回执）。\n" +
                "备份文件写入 <root>\\_upgrade-console\\backups\\。该操作只读源库、只写备份文件。",
                "backup");
            if (!confirmed)
            {
                return;
            }

            TxtBackupState.Text = "正在备份…";
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var backupDir = Path.Combine(env.Value.Root, PluginService.WorkDirName, "backups");
            Directory.CreateDirectory(backupDir);
            var target = Path.Combine(backupDir, $"memory-{stamp}.sqlite3");

            var r = await ProcessRunner.RunAsync(
                env.Value.VenvPython,
                $"-m scope_recall.maintenance.cli backup --database \"{env.Value.Db}\" --output \"{target}\"",
                timeoutMs: 600_000);

            if (r.Success && File.Exists(target))
            {
                var size = Exporter.HumanSize(new FileInfo(target).Length);
                TxtBackupState.Text = $"备份完成：{target}（{size}）\n{FirstLines(r.StdOut, 6)}";
            }
            else
            {
                TxtBackupState.Text = $"备份失败（exit {r.ExitCode}）：{FirstLine(r.StdErr) ?? FirstLine(r.StdOut) ?? "未知错误"}";
            }

        });
    }

    // ---------- 高级维护 ----------

    private void BtnMaintenance_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "OpsView.BtnMaintenance_Click", async () =>
        {
            if (e.OriginalSource is not Button { Tag: string command } || command.Length == 0)
            {
                return;
            }
            var env = ResolveEnv();
            if (env is null)
            {
                TxtMaintenanceState.Text = "未找到记忆库根目录或共享 venv。";
                return;
            }

            var (confirmed, _) = await ConfirmAsync(
                $"执行 {command}",
                $"将调用官方 CLI：scope_recall.maintenance.cli {command}。\n" +
                "该操作处理有界页面并写入记忆库，请确认你本人了解本次操作。",
                command);
            if (!confirmed)
            {
                return;
            }

            TxtMaintenanceState.Text = $"正在执行 {command}…";
            var r = await ProcessRunner.RunAsync(
                env.Value.VenvPython,
                $"-m scope_recall.maintenance.cli {command}",
                timeoutMs: 1_800_000);

            TxtMaintenanceState.Text = r.Success
                ? $"{command} 完成（exit 0）。\n{FirstLines(r.StdOut, 8)}"
                : $"{command} 失败（exit {r.ExitCode}）：{FirstLine(r.StdErr) ?? FirstLine(r.StdOut) ?? "未知错误"}";

        });
    }

    // ---------- 对话框 ----------

    private async Task<(bool Confirmed, string Auth)> ConfirmAsync(string title, string message, string verb)
    {
        var box = new TextBox { PlaceholderText = $"授权句（本人书写，≥6 字）。例：我确认对记忆库执行 {verb}" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
                    box,
                },
            },
            PrimaryButtonText = "确认执行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return (false, "");
        }
        var auth = (box.Text ?? "").Trim();
        if (auth.Length < 6)
        {
            TxtBackupState.Text = "授权句不足 6 字，操作已取消。";
            return (false, "");
        }
        return (true, auth);
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
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        return string.Join("\n", text.Split('\n').Take(count)).Trim();
    }
}
