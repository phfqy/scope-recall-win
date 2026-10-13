using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;
using Windows.System;

namespace ScopeRecallWin.Views;

public sealed partial class UpdateView : UserControl
{
    private const string RepoOwner = "phfqy";
    private const string RepoName = "scope-recall-win";

    private readonly IUpdateService? _update;
    private readonly IPluginService? _plugin;
    private readonly ObservableCollection<string> _pluginSteps = new();
    private UpdateCheckResult? _lastCheck;
    private PluginUpstreamCheck? _pluginCheck;
    private bool _checking;
    private bool _downloading;
    private bool _upgrading;

    public UpdateView()
    {
        InitializeComponent();
        _update = ServiceLocator.Get<IUpdateService>();
        _plugin = ServiceLocator.Get<IPluginService>();
        PluginStepList.ItemsSource = _pluginSteps;
        Loaded += UpdateView_Loaded;
    }

    private void UpdateView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_update is null)
        {
            TxtChannel.Text = "更新服务不可用";
            return;
        }
        TxtCurrentVersion.Text = _update.CurrentVersion;
        RefreshUpdateModeText();
        // 进入页面自动检查一次（失败静默，由用户手动重试）
        _ = CheckAsync();
    }

    // ---------- 控制台软件更新 ----------

    private void RefreshUpdateModeText()
    {
        var mode = App.Settings.UpdateMode == "notify" ? "仅通知（不自动安装）" : "自动（下载并静默替换）";
        TxtUpdateMode.Text = $"更新模式：{mode}。可在「设置」页切换。";
    }

    private async System.Threading.Tasks.Task CheckAsync()
    {
        if (_checking || _update is null)
        {
            return;
        }
        _checking = true;
        BtnCheck.IsEnabled = false;
        TxtChannel.Text = "正在检查…";

        var result = await _update.CheckAsync(RepoOwner, RepoName);

        TxtChannel.Text = $"通道：{result.Channel} · 检查时间 {result.CheckedAt:HH:mm:ss}";
        if (!string.IsNullOrEmpty(result.Error))
        {
            TxtChannel.Text += $" · {result.Error}";
            TxtChannel.Foreground = StatusVisuals.BrushOf("failed");
            _lastCheck = null;
            BtnUpgrade.IsEnabled = false;
            BtnOpenPage.IsEnabled = false;
        }
        else
        {
            _lastCheck = result;
            TxtLatestVersion.Text = result.LatestVersion;
            TxtChangelog.Text = string.IsNullOrWhiteSpace(result.Changelog) ? "（无发布说明）" : result.Changelog;
            var newer = result.IsNewer;
            BtnUpgrade.IsEnabled = newer && !string.IsNullOrEmpty(result.SetupUrl);
            BtnOpenPage.IsEnabled = !string.IsNullOrEmpty(result.ReleaseUrl);
            TxtChannel.Text += newer ? " · 有新版本" : " · 已是最新";
            TxtChannel.Foreground = newer
                ? StatusVisuals.BrushOf("pending")
                : StatusVisuals.BrushOf("ok");
        }

        _checking = false;
        BtnCheck.IsEnabled = true;
    }

    private void BtnCheck_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "UpdateView.BtnCheck_Click", async () =>
        {
            await CheckAsync();

        });
    }

    private void BtnUpgrade_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "UpdateView.BtnUpgrade_Click", async () =>
        {
            if (_update is null || _lastCheck?.SetupUrl is not { } url || _downloading)
            {
                return;
            }
            _downloading = true;
            BtnUpgrade.IsEnabled = false;
            BtnCheck.IsEnabled = false;
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.Value = 0;
            TxtDownloadState.Visibility = Visibility.Visible;
            TxtDownloadState.Text = "正在下载安装包…";

            var progress = new Progress<double>(p => DownloadProgress.Value = p);
            var download = await _update.DownloadAsync(url, _lastCheck.Sha256Sum, progress);

            if (download.FilePath is null || download.Error is not null)
            {
                TxtDownloadState.Text = $"下载失败：{download.Error ?? "未知错误"}（未做任何更改）";
            TxtDownloadState.Foreground = StatusVisuals.BrushOf("failed");
                _downloading = false;
                BtnUpgrade.IsEnabled = true;
                BtnCheck.IsEnabled = true;
                return;
            }

            TxtDownloadState.Text = "下载完成，正在启动静默安装…";
            var started = _update.StartSilentInstall(download.FilePath);
            if (!started)
            {
                TxtDownloadState.Text = $"安装器启动失败，请手动运行：{download.FilePath}";
                _downloading = false;
                BtnUpgrade.IsEnabled = true;
                BtnCheck.IsEnabled = true;
                return;
            }

            TxtDownloadState.Text = "安装器已启动，本程序即将退出完成替换…";
            await System.Threading.Tasks.Task.Delay(1200);
            Application.Current.Exit();

        });
    }

    private void BtnOpenPage_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "UpdateView.BtnOpenPage_Click", async () =>
        {
            if (_lastCheck?.ReleaseUrl is { } url)
            {
                await Launcher.LaunchUriAsync(new Uri(url));
            }

        });
    }

    // ---------- 记忆插件更新 ----------

    private void BtnPluginCheck_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "UpdateView.BtnPluginCheck_Click", async () =>
        {
            if (_plugin is null)
            {
                TxtPluginState.Text = "插件服务不可用。";
                return;
            }
            BtnPluginCheck.IsEnabled = false;
            TxtPluginState.Text = "正在检查插件版本…";

            _pluginCheck = await _plugin.CheckUpstreamAsync();
            if (_pluginCheck.Error is not null)
            {
                TxtPluginState.Text = $"检查失败：{_pluginCheck.Error}";
            TxtPluginState.Foreground = StatusVisuals.BrushOf("failed");
                PluginUpgradePanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtPluginCurrent.Text = _pluginCheck.InstalledVersion ?? "未安装";
                TxtPluginLatest.Text = _pluginCheck.LatestVersion ?? "—";
                var channels = new List<string>();
                foreach (var probe in _pluginCheck.ChannelProbes)
                {
                    channels.Add(probe.Ok ? $"{probe.Name} {probe.LatencyMs}ms" : $"{probe.Name} 超时");
                }
                TxtPluginState.Text = _pluginCheck.HasUpdate
                    ? $"发现新版本 {_pluginCheck.LatestVersion}（已装 {_pluginCheck.InstalledVersion}）。通道：{string.Join(" / ", channels)}"
                    : $"已是最新（{_pluginCheck.InstalledVersion}）。通道：{string.Join(" / ", channels)}";
                PluginUpgradePanel.Visibility = _pluginCheck.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
                RefreshPluginUpgradeButton();
            }

            BtnPluginCheck.IsEnabled = true;

        });
    }

    private void RefreshPluginUpgradeButton()
    {
        BtnPluginUpgrade.IsEnabled = _pluginCheck?.HasUpdate == true && !_upgrading;
    }

    private void BtnOpenPluginRepo_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/410979729/scope-recall-hermes");

    private void BtnOpenPluginPypi_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://pypi.org/project/hermes-scope-recall/");

    /// <summary>用系统默认浏览器打开链接（受限环境下静默失败并记录）。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.LogCrash("UpdateView.OpenUrl:" + url, ex);
        }
    }

    private void BtnPluginUpgrade_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "UpdateView.BtnPluginUpgrade_Click", async () =>
        {
            if (_plugin is null || _upgrading)
            {
                return;
            }
            var env = await _plugin.ProbeEnvironmentAsync();
            if (env.Root is null)
            {
                TxtPluginState.Text = $"无法定位记忆库根目录：{env.Error}";
                return;
            }

            if (!await Confirm.AskAsync(
                    this,
                    "升级记忆插件",
                    $"将把记忆插件从 {_pluginCheck?.InstalledVersion} 升级到 {_pluginCheck?.LatestVersion}。\n" +
                    "流程：检查 → 备份 → 下载校验 → 停止常驻服务 → 替换文件 → 校验版本。",
                    "升级期间会暂停记忆写入；若任一环节失败会立即停止并保留备份，但不会自动回滚。",
                    "开始升级"))
            {
                return;
            }

            _upgrading = true;
            RefreshPluginUpgradeButton();
            _pluginSteps.Clear();
            TxtPluginState.Text = "升级进行中…";

            var request = new PluginUpgradeRequest(
                env.Root,
                "",
                true,          // 已通过界面二次确认
                "pypi",
                Force: false);

            var progress = new Progress<PluginUpgradeProgress>(p =>
            {
                _pluginSteps.Add($"[{p.StepIndex}/{p.StepCount}] {p.Step} · {p.State} {p.Detail}".Trim());
                PluginStepList.ScrollIntoView(_pluginSteps[^1]);
            });

            var result = await _plugin.UpgradeAsync(request, progress);

            if (result.Success)
            {
                TxtPluginState.Text = $"升级完成：{result.FromVersion} → {result.ToVersion}";
            TxtPluginState.Foreground = StatusVisuals.BrushOf("ok");
                PluginUpgradePanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtPluginState.Text = $"升级中止：{result.Error}";
            TxtPluginState.Foreground = StatusVisuals.BrushOf("failed");
                foreach (var line in result.Log)
                {
                    _pluginSteps.Add($"  {line}");
                }
            }

            _upgrading = false;
            RefreshPluginUpgradeButton();

        });
    }
}
