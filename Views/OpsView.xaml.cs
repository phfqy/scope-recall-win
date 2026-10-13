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
    /// <summary>备份文件列表项。</summary>
    private sealed record BackupFile(string Path, string Name, string Meta);

    private readonly IPluginService? _plugin;
    private List<BackupFile> _backups = new();
    private string? _selectedBackup;

    public OpsView()
    {
        InitializeComponent();
        _plugin = ServiceLocator.Get<IPluginService>();
        Loaded += (_, _) =>
        {
            UiThread.Run(DispatcherQueue, "OpsView.LoadBackups", RefreshBackupsAsync);
            UiThread.Run(DispatcherQueue, "OpsView.LoadEntries", RefreshEntriesAsync);
        };
    }

    // ---------- 从备份恢复 ----------

    /// <summary>备份目录：共享库根目录下的 _upgrade-console\backups。</summary>
    private static string? BackupDirOf(string root) => Path.Combine(root, PluginService.WorkDirName, "backups");

    private async Task RefreshBackupsAsync()
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        var list = new List<BackupFile>();
        if (root is not null)
        {
            var dir = BackupDirOf(root);
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.sqlite3")
                                 .OrderByDescending(File.GetLastWriteTime))
                    {
                        var info = new FileInfo(file);
                        list.Add(new BackupFile(
                            file,
                            info.Name,
                            $"大小 {Exporter.HumanSize(info.Length)} · 备份于 {info.LastWriteTime:yyyy-MM-dd HH:mm}"));
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogCrash("OpsView.RefreshBackups", ex);
            }
        }

        _backups = list;
        BackupList.ItemsSource = _backups;
        TxtRestoreState.Text = _backups.Count == 0
            ? "备份文件夹里还没有备份文件；可先「立即备份」，或点「选择其他文件…」指定已有备份。"
            : $"共 {_backups.Count} 份备份，点击列表选择要恢复的那一份。";
        TxtRestoreState.Foreground = StatusVisuals.BrushOf(_backups.Count == 0 ? "pending" : "ok");
        await Task.CompletedTask;
    }

    private void BackupList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BackupFile file)
        {
            _selectedBackup = file.Path;
            TxtRestoreState.Text = $"已选择备份：{file.Name}（{file.Meta}）";
            TxtRestoreState.Foreground = StatusVisuals.BrushOf("pending");
        }
    }

    private void BtnRefreshBackups_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "OpsView.BtnRefreshBackups_Click", RefreshBackupsAsync);

    private void BtnOpenBackupDir_Click(object sender, RoutedEventArgs e)
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null)
        {
            TxtBackupState.Text = "未找到记忆库根目录。";
            return;
        }
        var dir = BackupDirOf(root)!;
        Directory.CreateDirectory(dir);
        OpenInExplorer(dir);
    }

    private void BtnPickBackup_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "OpsView.BtnPickBackup_Click", async () =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".sqlite3");
                picker.FileTypeFilter.Add(".db");
                if (App.MainWindow is not null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }
                var file = await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    _selectedBackup = file.Path;
                    TxtRestoreState.Text = $"已选择备份：{file.Name}";
                    TxtRestoreState.Foreground = StatusVisuals.BrushOf("pending");
                }
            }
            catch (Exception ex)
            {
                App.LogCrash("OpsView.PickBackup", ex);
                TxtRestoreState.Text = $"文件选择器不可用：{ex.Message}";
                TxtRestoreState.Foreground = StatusVisuals.BrushOf("failed");
            }
        });

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "OpsView.BtnRestore_Click", async () =>
        {
            var env = ResolveEnv();
            if (env is null)
            {
                TxtRestoreState.Text = "未找到记忆库根目录或共享运行环境。";
                TxtRestoreState.Foreground = StatusVisuals.BrushOf("failed");
                return;
            }
            if (_selectedBackup is null || !File.Exists(_selectedBackup))
            {
                TxtRestoreState.Text = "请先在列表中选择一份备份（或点「选择其他文件…」）。";
                TxtRestoreState.Foreground = StatusVisuals.BrushOf("pending");
                return;
            }

            var snapshotName = Path.GetFileName(_selectedBackup);
            if (!await Confirm.AskAsync(
                    this,
                    "从备份恢复记忆库",
                    $"将把记忆库恢复为备份「{snapshotName}」的状态。恢复前会自动为当前记忆库再做一次备份，因此随时可以恢复到现在的状态。",
                    "恢复后，这份备份之后新增或修改的记忆会被覆盖（包括所有已接入助手的记忆）。",
                    "确认恢复"))
            {
                return;
            }

            // 1) 恢复前自动备份当前库
            TxtRestoreState.Text = "正在为当前记忆库做恢复前备份…";
            TxtRestoreState.Foreground = StatusVisuals.BrushOf("pending");
            var preDir = BackupDirOf(env.Value.Root)!;
            Directory.CreateDirectory(preDir);
            var preBackup = Path.Combine(preDir, $"memory-before-restore-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.sqlite3");
            var pre = await ProcessRunner.RunAsync(
                env.Value.VenvPython,
                MaintenanceCli.BackupArgs(env.Value.Db, preBackup),
                timeoutMs: 600_000);
            if (!pre.Success || !File.Exists(preBackup))
            {
                TxtRestoreState.Text = $"恢复中止：恢复前备份失败（{MaintenanceCli.FailureText(pre)}）。记忆库未被改动。";
                TxtRestoreState.Foreground = StatusVisuals.BrushOf("failed");
                return;
            }

            // 2) 校验备份文件（不带 --apply 为预演）
            TxtRestoreState.Text = "正在校验备份文件…";
            var dry = await ProcessRunner.RunAsync(
                env.Value.VenvPython,
                MaintenanceCli.RollbackArgs(env.Value.Db, _selectedBackup, apply: false),
                timeoutMs: 600_000);
            if (!dry.Success)
            {
                TxtRestoreState.Text = $"恢复中止：备份文件校验未通过（{MaintenanceCli.FailureText(dry)}）。记忆库未被改动。";
                TxtRestoreState.Foreground = StatusVisuals.BrushOf("failed");
                return;
            }

            // 3) 执行恢复
            TxtRestoreState.Text = "正在恢复…";
            var apply = await ProcessRunner.RunAsync(
                env.Value.VenvPython,
                MaintenanceCli.RollbackArgs(env.Value.Db, _selectedBackup, apply: true),
                timeoutMs: 900_000);

            if (apply.Success)
            {
                TxtRestoreState.Text = $"✓ 已恢复为备份「{snapshotName}」的状态。恢复前的记忆已另存为：{Path.GetFileName(preBackup)}";
                TxtRestoreState.Foreground = StatusVisuals.BrushOf("ok");
                await RefreshBackupsAsync();
            }
            else
            {
                TxtRestoreState.Text = $"✗ 恢复失败：{MaintenanceCli.FailureText(apply)}。" +
                                       $"恢复前的备份仍保留在：{Path.GetFileName(preBackup)}";
                TxtRestoreState.Foreground = StatusVisuals.BrushOf("failed");
            }
        });

    /// <summary>用资源管理器打开目录（失败时静默记录）。</summary>
    private static void OpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.LogCrash("OpsView.OpenInExplorer", ex);
        }
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
        => UiThread.Run(DispatcherQueue, "OpsView.BtnRefreshEntries_Click", RefreshEntriesAsync);

    private async Task RefreshEntriesAsync()
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
            Title = string.IsNullOrWhiteSpace(en.DisplayName) ? en.Entry : $"{en.DisplayName}（{en.Entry}）",
            Meta = $"类型 {en.Host} · 数据目录 {en.Home} · 常驻服务 {(en.Alive ? "运行中" : "未运行")}",
        }).ToList();
        TxtEntriesState.Text = entries.Count == 0
            ? "这个共享库还没有接入任何助手。"
            : $"共 {entries.Count} 个助手已接入。";
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

            if (!await Confirm.AskAsync(
                    this,
                    "备份记忆库",
                    "将生成一份当前记忆库的一致性快照（含完整性校验与校验值），备份文件保存到备份文件夹。",
                    null,
                    "开始备份"))
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
                MaintenanceCli.BackupArgs(env.Value.Db, target),
                timeoutMs: 600_000);

            if (r.Success && File.Exists(target))
            {
                var size = Exporter.HumanSize(new FileInfo(target).Length);
                TxtBackupState.Text = $"备份完成：{target}（{size}）\n{FirstLines(r.StdOut, 6)}";
            }
            else
            {
                TxtBackupState.Text = $"备份失败：{MaintenanceCli.FailureText(r)}";
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

            if (!await Confirm.AskAsync(
                    this,
                    MaintenanceTitle(command),
                    MaintenanceMessage(command),
                    "该操作会写入记忆库；请尽量在无人使用记忆库时执行，执行前建议先做一次备份。",
                    "确认执行"))
            {
                return;
            }

            TxtMaintenanceState.Text = $"正在执行 {command}…";
            // 官方契约：config 类子命令的 --config 必给（漏传即 exit 2 只打 usage），
            // 且不带 --apply 只是预演；respace-embeddings 缺省动作是只读 status，须显式 --start。
            var args = MaintenanceCli.RequiresConfig(command)
                ? MaintenanceCli.Build(env.Value.Root, command, apply: true, extra: MaintenanceCli.ActionSwitch(command))
                : $"{MaintenanceCli.Module} {command}";
            var r = await ProcessRunner.RunAsync(
                env.Value.VenvPython,
                args,
                timeoutMs: 1_800_000);

            if (r.Success && MaintenanceCli.IsPreviewOnly(r.StdOut))
            {
                TxtMaintenanceState.Text =
                    $"{command} 只完成了预演（回执 applied=false），记忆库未被改动。\n{FirstLines(r.StdOut, 8)}";
                return;
            }

            TxtMaintenanceState.Text = r.Success
                ? $"{command} 完成（exit 0）。\n{FirstLines(r.StdOut, 8)}"
                : $"{command} 失败：{MaintenanceCli.FailureText(r)}";

        });
    }

    // ---------- 对话框 ----------

    /// <summary>维护操作的中文标题。</summary>
    private static string MaintenanceTitle(string command) => command switch
    {
        "requalify" => "重新判定候选记忆",
        "respace-embeddings" => "重建向量索引",
        "retry-failures" => "重试失败任务",
        "upgrade-store" => "升级记忆库结构",
        _ => "执行维护操作",
    };

    /// <summary>维护操作的中文说明。</summary>
    private static string MaintenanceMessage(string command) => command switch
    {
        "requalify" => "将重新评估所有待确认的候选记忆，符合条件者转为正式记忆，不符合者继续等待或归档。",
        "respace-embeddings" => "将按当前模型重新计算全部记忆的向量（耗时与记忆条数相关，期间语义检索可能不完整）。",
        "retry-failures" => "将把失败的后台任务重新放回队列，按原参数再处理一次。",
        "upgrade-store" => "将把记忆库的数据结构升级到当前版本（升级前会自动备份）。",
        _ => $"将执行维护操作：{command}。",
    };

    private static string FirstLines(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        return string.Join("\n", text.Split('\n').Take(count)).Trim();
    }
}
