using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;
using Windows.Storage.Pickers;

namespace ScopeRecallWin.Views;

/// <summary>
/// 云同步视图：把本机共享记忆同步到云端，并让远端助手写入的记忆回流到本机。
/// </summary>
/// <remarks>
/// 安全设计（与全站约束一致）：
/// <list type="number">
/// <item><b>控制台不碰记忆库</b>：所有读写都发生在托管的 Python 子进程里。真实库以
/// <c>mode=ro</c> 打开，写回只经官方 <c>propose_memory</c>。</item>
/// <item><b>先备份再同步</b>：同步按钮先调官方 backup CLI 做一致性快照，备份失败即中止
/// —— 远端写回会改动共享库，必须有可回滚的基线。</item>
/// <item><b>授权句必填</b>：与「变更」页同一护栏思路，不代笔、不预填，只作为本次操作留痕。</item>
/// <item><b>口令只进凭据管理器</b>：PasswordBox 的值直接写 Windows 凭据管理器，
/// 不进 settings.json、不进命令行参数、不进日志。</item>
/// </list>
/// </remarks>
public sealed partial class CloudSyncView : UserControl
{
    private readonly ICloudSyncService? _sync;
    private readonly ICredentialService? _credentials;
    private readonly IPluginService? _plugin;

    private bool _busy;

    public CloudSyncView()
    {
        InitializeComponent();
        _sync = ServiceLocator.Get<ICloudSyncService>();
        _credentials = ServiceLocator.Get<ICredentialService>();
        _plugin = ServiceLocator.Get<IPluginService>();

        PickerBackend.SetItems(
            new[] { "坚果云 / WebDAV", "S3 兼容（R2 / OSS / COS）", "本地目录（测试用）" },
            selectedIndex: 0,
            tags: new[] { "webdav", "s3", "local" });
        PickerBackend.SelectionChanged += (_, _) => ApplyBackendVisibility();

        Loaded += (_, _) => UiThread.Run(DispatcherQueue, "CloudSyncView.Loaded", LoadAsync);
    }

    private async Task LoadAsync()
    {
        ApplyConfigToForm();
        ApplyBackendVisibility();
        await RefreshStatusAsync();
    }

    // ---------- 配置 ----------

    private void ApplyConfigToForm()
    {
        if (_sync is null)
        {
            TxtState.Text = "云同步服务不可用。";
            return;
        }
        var c = _sync.GetConfig();
        PickerBackend.SelectByTag(c.Backend);
        TxtUrl.Text = c.Url ?? "";
        TxtUser.Text = c.User ?? "";
        TxtBucket.Text = c.Bucket ?? "";
        TxtEndpoint.Text = c.Endpoint ?? "";
        TxtRoot.Text = c.Root ?? "";
        NumInterval.Value = c.IntervalSeconds < 60 ? 60 : c.IntervalSeconds;
        ChkIncludeEvents.IsChecked = c.IncludeEvents;

        var python = _sync.ResolvePython(PathDiscovery.FindMemoryRoot(App.Settings) ?? "");
        TxtPython.Text = python is null ? "未找到 Python 解释器" : $"解释器：{python}";
    }

    private void ApplyBackendVisibility()
    {
        var tag = PickerBackend.SelectedTag ?? "webdav";
        var isWebdav = tag == "webdav";
        var isS3 = tag == "s3";

        TxtUrl.Visibility = isWebdav ? Visibility.Visible : Visibility.Collapsed;
        TxtUser.Visibility = isWebdav ? Visibility.Visible : Visibility.Collapsed;
        PwdSecret.Visibility = isWebdav || isS3 ? Visibility.Visible : Visibility.Collapsed;
        TxtBucket.Visibility = isS3 ? Visibility.Visible : Visibility.Collapsed;
        TxtEndpoint.Visibility = isS3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private CloudSyncConfig ReadForm() => new(
        Enabled: true,
        Backend: PickerBackend.SelectedTag ?? "webdav",
        Url: NullIfBlank(TxtUrl.Text),
        User: NullIfBlank(TxtUser.Text),
        Bucket: NullIfBlank(TxtBucket.Text),
        Endpoint: NullIfBlank(TxtEndpoint.Text),
        Region: "auto",
        Root: TxtRoot.Text?.Trim() ?? "",
        IntervalSeconds: (int)Math.Max(60, NumInterval.Value),
        IncludeEvents: ChkIncludeEvents.IsChecked == true,
        PythonPath: _sync?.ResolvePython(PathDiscovery.FindMemoryRoot(App.Settings) ?? ""));

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void BtnSave_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "CloudSyncView.Save", async () =>
        {
            if (_sync is null)
            {
                return;
            }
            var config = ReadForm();
            _sync.SaveConfig(config);

            // 口令：有输入才覆盖凭据管理器；留空表示「保持原值」，不误删已有口令
            var secret = PwdSecret.Password;
            if (!string.IsNullOrEmpty(secret) && _credentials is not null)
            {
                var account = config.Backend == "s3" ? "s3-secret-key" : (config.User ?? "default");
                await _credentials.SaveAsync(CloudSyncService.CredentialServiceName, account, secret);
                PwdSecret.Password = "";
            }

            SetConfigState("✓ 配置已保存。" +
                           (string.IsNullOrEmpty(secret) ? "（口令未改动）" : "（口令已存入 Windows 凭据管理器）"),
                "ok");
        });

    // ---------- 自检 ----------

    private void BtnPreflight_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "CloudSyncView.Preflight", async () =>
        {
            // 自检结果必须写回「环境自检」卡片里的 TxtPreflight ——
            // 之前误写成 SetConfigState（那是「云端存储」卡片底部的提示），
            // 结果文字出现在下面很远的卡片里，点按钮看起来像「没有效果」。
            SetPreflightState("正在自检…", "pending");
            BtnPreflight.IsEnabled = false;
            try
            {
                var root = PathDiscovery.FindMemoryRoot(App.Settings);
                if (root is null)
                {
                    SetPreflightState("✗ 未找到记忆库根目录。请先到「安装」页选择部署目录并完成部署。", "failed");
                    return;
                }
                if (_sync is null)
                {
                    SetPreflightState("✗ 云同步服务未注册（应用启动异常），请重启应用。", "failed");
                    return;
                }

                var result = await _sync.PreflightAsync(root);

                TxtPython.Text = result.PythonPath is null
                    ? "未找到 Python 解释器"
                    : $"解释器：{result.PythonPath}" +
                      (string.IsNullOrEmpty(result.PythonVersion) ? "" : $"（{result.PythonVersion}）");

                if (result.Ready)
                {
                    var script = result.BridgeScript ?? "（随应用分发）";
                    SetPreflightState($"✓ 环境就绪：解释器、桥脚本与依赖均可用。\n桥脚本：{script}", "ok");
                }
                else
                {
                    SetPreflightState("✗ " + (result.Message ?? "环境不满足云同步要求。"), "failed");
                }
            }
            catch (Exception ex)
            {
                // 自检异常必须在「环境自检」卡片内可见；不能只写 crash.log，
                // 否则用户体感仍是“按钮点了没反应”。
                SetPreflightState($"✗ 自检异常：{ex.Message}", "failed");
                App.LogCrash("CloudSyncView.Preflight", ex);
            }
            finally
            {
                BtnPreflight.IsEnabled = true;
            }
        });

    private void BtnPickPython_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "CloudSyncView.PickPython", async () =>
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");

            var file = await picker.PickSingleFileAsync();
            if (file is null || _sync is null)
            {
                return;
            }
            var config = ReadForm() with { PythonPath = file.Path };
            _sync.SaveConfig(config);
            TxtPython.Text = $"解释器：{file.Path}";
            // 与自检同属「环境自检」卡片，反馈也写这张卡片
            SetPreflightState("✓ 已切换 Python 解释器，建议重新自检。", "ok");
        });

    // ---------- 连接测试 ----------

    private void BtnTest_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "CloudSyncView.Test", async () =>
        {
            var root = PathDiscovery.FindMemoryRoot(App.Settings);
            if (root is null || _sync is null)
            {
                SetConfigState("未找到记忆库根目录。", "failed");
                return;
            }
            var config = ReadForm();
            if (config.Backend == "webdav" && string.IsNullOrWhiteSpace(config.Url))
            {
                SetConfigState("请先填写 WebDAV 服务地址。", "failed");
                return;
            }
            if (config.Backend == "s3" && (string.IsNullOrWhiteSpace(config.Bucket) || string.IsNullOrWhiteSpace(config.Endpoint)))
            {
                SetConfigState("S3 兼容存储需要同时填写 Bucket 与 Endpoint。", "failed");
                return;
            }

            SetConfigState("正在测试云端连接…", "pending");
            _sync.SaveConfig(config);

            // 用一轮 dry-run 的等价物：只做环境自检 + 配置完整性，不触发真实上传
            var pre = await _sync.PreflightAsync(root);
            if (!pre.Ready)
            {
                SetConfigState("✗ " + (pre.Message ?? "环境不满足要求。"), "failed");
                return;
            }
            SetConfigState("✓ 环境与配置检查通过。实际连通性会在首次同步时验证（会先自动备份）。", "ok");
        });

    // ---------- 执行同步（写操作） ----------

    private void BtnSync_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "CloudSyncView.Sync", async () =>
        {
            if (_busy)
            {
                return;
            }
            var root = PathDiscovery.FindMemoryRoot(App.Settings);
            if (root is null || _sync is null)
            {
                SetSyncState("未找到记忆库根目录。", "failed");
                return;
            }

            // 护栏 1：授权说明必填（不代笔、不预填）
            var authorization = (TxtAuthorization.Text ?? "").Trim();
            if (authorization.Length < 4)
            {
                SetSyncState("请先填写授权说明（至少 4 个字），写明本次同步的用途。", "failed");
                return;
            }

            var pre = await _sync.PreflightAsync(root);
            if (!pre.Ready)
            {
                SetSyncState("✗ " + (pre.Message ?? "环境不满足要求。"), "failed");
                return;
            }

            // 护栏 2：先备份，再同步。备份失败即中止（写回会改动共享库）
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScopeRecallWin", "cloudsync-backups");
            Directory.CreateDirectory(backupDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(backupDir, $"memory-{stamp}.sqlite3");

            _busy = true;
            BtnSync.IsEnabled = false;
            SyncProgress.Visibility = Visibility.Visible;
            SetSyncState("正在检查云桥成员…", "pending");

            try
            {
                var venvPython = PathDiscovery.FindVenvPython(root);
                if (venvPython is null)
                {
                    SetSyncState("未找到共享运行环境，无法调用官方备份命令。", "failed");
                    return;
                }

                // 写通道前提：云桥成员必须先接入共享库，否则 propose_memory 会被拒。
                // 这一步只写 installation.json（官方 attach 命令），不动记忆正文。
                var entry = await _sync.EnsureCloudEntryAsync(root);
                if (!entry.Attached)
                {
                    SetSyncState("✗ " + entry.Message, "failed");
                    return;
                }

                SetSyncState("正在备份本机记忆库…", "pending");

                var backupResult = await ProcessRunner.RunAsync(
                    venvPython,
                    MaintenanceCli.BackupArgs(PathDiscovery.MemoryDbPath(root), backupPath),
                    timeoutMs: 600_000);

                if (!backupResult.Success || !File.Exists(backupPath))
                {
                    SetSyncState($"✗ 备份失败，已中止同步：{MaintenanceCli.FailureText(backupResult)}", "failed");
                    return;
                }

                SetSyncState($"备份完成（{Path.GetFileName(backupPath)}），正在同步…", "pending");
                var round = await _sync.RunOnceAsync(root, backupPath);
                RenderRound(round);

                if (round.Ok)
                {
                    SetSyncState($"✓ 同步完成：上传 {round.Pushed} 条、拉取 {round.Pulled} 条、" +
                                 $"写入本机 {round.WrittenToStore} 条。", "ok");
                }
                else if (round.Partial)
                {
                    SetSyncState($"⚠ 同步部分完成：{string.Join("；", round.Errors.Take(3))}", "warn");
                }
                else
                {
                    SetSyncState($"✗ 同步失败：{string.Join("；", round.Errors.Take(3))}", "failed");
                }
            }
            catch (Exception ex)
            {
                SetSyncState($"✗ 同步异常：{ex.Message}", "failed");
            }
            finally
            {
                SyncProgress.Visibility = Visibility.Collapsed;
                BtnSync.IsEnabled = true;
                _busy = false;
            }
        });

    private void BtnOpenBackups_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "CloudSyncView.OpenBackups", async () =>
        {
            await Task.CompletedTask;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScopeRecallWin", "cloudsync-backups");
            Directory.CreateDirectory(dir);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch
            {
                SetSyncState($"备份目录：{dir}", "pending");
            }
        });

    // ---------- 状态渲染 ----------

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "CloudSyncView.Refresh", RefreshStatusAsync);

    private async Task RefreshStatusAsync()
    {
        await Task.CompletedTask;
        if (_sync is null)
        {
            return;
        }
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null)
        {
            TxtState.Text = "未找到记忆库根目录，请先到「安装」页完成部署。";
            SyncLed.Background = StatusVisuals.BrushOf("failed");
            return;
        }

        var round = _sync.ReadLastStatus(root);
        if (round is null)
        {
            TxtState.Text = "还没有同步记录。配置好云端后，点下方「备份并同步一轮」开始。";
            SyncLed.Background = StatusVisuals.BrushOf("pending");
            TxtLastRound.Text = "";
            return;
        }
        RenderRound(round);
        TxtState.Text = round.Ok
            ? $"上次同步成功（{round.WrittenAt}）"
            : round.Partial
                ? $"上次同步部分完成（{round.WrittenAt}）"
                : $"上次同步失败（{round.WrittenAt}）";
        SyncLed.Background = StatusVisuals.BrushOf(round.Ok ? "ok" : round.Partial ? "warn" : "failed");
    }

    private void RenderRound(CloudSyncRound round)
    {
        TxtPushed.Text = round.Pushed.ToString();
        TxtPulled.Text = round.Pulled.ToString();
        TxtWritten.Text = round.WrittenToStore.ToString();
        TxtConflicts.Text = round.Conflicts.ToString();

        TxtLastRound.Text = round.ClaimsActive > 0
            ? $"本机记忆库：生效记忆 {round.ClaimsActive} 条 · 原始事件 {round.SourceEvents} 条 · " +
              $"耗时 {round.DurationSeconds:0.0} 秒 · 未变化 {round.Unchanged} 条"
            : $"耗时 {round.DurationSeconds:0.0} 秒 · 未变化 {round.Unchanged} 条";

        if (round.Errors.Count > 0)
        {
            TxtErrors.Text = "细节：" + string.Join("\n", round.Errors.Take(6));
            TxtErrors.Visibility = Visibility.Visible;
        }
        else
        {
            TxtErrors.Visibility = Visibility.Collapsed;
        }
    }

    private void SetConfigState(string message, string state)
    {
        TxtConfigState.Text = message;
        TxtConfigState.Foreground = StatusVisuals.BrushOf(state);
    }

    /// <summary>「环境自检」卡片自己的结果行（与「云端存储」卡片的提示分开，避免反馈错位）。</summary>
    private void SetPreflightState(string message, string state)
    {
        TxtPreflight.Text = message;
        TxtPreflight.Foreground = StatusVisuals.BrushOf(state);
    }

    private void SetSyncState(string message, string state)
    {
        TxtSyncState.Text = message;
        TxtSyncState.Foreground = StatusVisuals.BrushOf(state);
    }
}
