using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;
using Windows.Storage.Pickers;

namespace ScopeRecallWin.Views;

public sealed partial class InstallView : UserControl
{
    private readonly IPluginService? _plugin;
    private readonly ObservableCollection<string> _planSteps = new();
    private readonly ObservableCollection<string> _deployLog = new();
    private string? _plannedRoot;
    private bool _deploying;

    public InstallView()
    {
        InitializeComponent();
        _plugin = ServiceLocator.Get<IPluginService>();
        PlanList.ItemsSource = _planSteps;
        DeployLogList.ItemsSource = _deployLog;
        Loaded += InstallView_Loaded;
    }

    private async void InstallView_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshEnvironmentAsync();
        // 默认目标目录：已检测到的根目录优先，否则 E:\ScopeRecall
        TxtTargetRoot.Text = PathDiscovery.FindMemoryRoot(App.Settings) ?? @"E:\ScopeRecall";
        UpdateRootState();
    }

    // ---------- 环境检测 ----------

    private async Task RefreshEnvironmentAsync()
    {
        if (_plugin is null)
        {
            TxtEnvError.Text = "插件服务不可用。";
            TxtEnvError.Visibility = Visibility.Visible;
            return;
        }

        var env = await _plugin.ProbeEnvironmentAsync();
        TxtRoot.Text = env.Root ?? "未检测到";
        TxtInstalled.Text = env.InstalledVersion ?? "未安装";
        TxtUv.Text = env.ExternalUvAvailable ? "可用" : "缺失（升级换包需要）";

        if (env.Error is not null)
        {
            TxtEnvError.Text = env.Error;
            TxtEnvError.Visibility = Visibility.Visible;
        }
        else
        {
            TxtEnvError.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnRefreshEnv_Click(object sender, RoutedEventArgs e)
    {
        await RefreshEnvironmentAsync();
    }

    // ---------- 目录选择 ----------

    private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            if (App.MainWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                TxtTargetRoot.Text = folder.Path;
                UpdateRootState();
            }
        }
        catch (Exception ex)
        {
            TxtRootState.Text = $"目录选择器不可用：{ex.Message}（可直接在输入框中键入路径）";
        }
    }

    private void BtnUseDefault_Click(object sender, RoutedEventArgs e)
    {
        TxtTargetRoot.Text = @"E:\ScopeRecall";
        UpdateRootState();
    }

    private void BtnUseAppDir_Click(object sender, RoutedEventArgs e)
    {
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "ScopeRecallWin", "data");
        TxtTargetRoot.Text = appDir;
        UpdateRootState();
    }

    private void UpdateRootState()
    {
        var root = (TxtTargetRoot.Text ?? "").Trim();
        if (root.Length == 0)
        {
            TxtRootState.Text = "请选择部署目录。";
            return;
        }
        if (PathDiscovery.IsMemoryRoot(root))
        {
            TxtRootState.Text = "该目录已存在记忆库：将进入复用模式（校验 + 权限自检，不重装）。";
        }
        else if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            TxtRootState.Text = "目录已存在且非空：将作为全新部署目标（不会删除已有文件，仅新增 shared\\ 与 shared-venv\\）。";
        }
        else
        {
            TxtRootState.Text = "全新部署目标。";
        }
    }

    // ---------- 部署计划 ----------

    private async void BtnPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is null)
        {
            return;
        }
        var root = (TxtTargetRoot.Text ?? "").Trim();
        if (root.Length == 0)
        {
            TxtPlanWarning.Text = "请先选择部署目录。";
            TxtPlanWarning.Visibility = Visibility.Visible;
            return;
        }

        var plan = await _plugin.PlanInstallAsync(root);
        _plannedRoot = root;
        _planSteps.Clear();
        foreach (var step in plan.Steps)
        {
            _planSteps.Add(step);
        }
        if (plan.FreeDiskBytes is { } free)
        {
            _planSteps.Add($"目标磁盘可用空间：{free / (1024 * 1024 * 1024.0):F1} GB");
        }
        if (plan.Warning is { } warning)
        {
            TxtPlanWarning.Text = warning;
            TxtPlanWarning.Visibility = Visibility.Visible;
        }
        else
        {
            TxtPlanWarning.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 执行部署 ----------

    private async void BtnDeploy_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is null || _deploying)
        {
            return;
        }
        var root = _plannedRoot ?? (TxtTargetRoot.Text ?? "").Trim();
        if (root.Length == 0)
        {
            TxtDeployState.Text = "请先生成部署计划。";
            return;
        }
        var auth = (TxtAuthorization.Text ?? "").Trim();
        if (auth.Length < 6)
        {
            TxtDeployState.Text = "授权句不足 6 字：请用一句话确认你本人了解本次部署。";
            return;
        }
        if (ChkConfirm.IsChecked != true)
        {
            TxtDeployState.Text = "请勾选确认框。";
            return;
        }

        _deploying = true;
        BtnDeploy.IsEnabled = false;
        _deployLog.Clear();
        TxtDeployState.Text = "部署进行中…";

        var progress = new Progress<PluginInstallProgress>(p =>
        {
            _deployLog.Add($"[{p.Step}] {p.State} {p.Detail}".Trim());
            DeployLogList.ScrollIntoView(_deployLog[^1]);
        });

        var result = await _plugin.ApplyInstallAsync(root, auth, progress);

        if (result.Success)
        {
            TxtDeployState.Text = $"部署完成：{(result.InstalledVersion is null ? "" : $"版本 {result.InstalledVersion} · ")}{result.Error ?? "权限自检通过"}";
            // 部署成功后把根目录写入设置，供其他页面使用
            App.Settings.MemoryRootPath = root;
            App.Settings.Save();
            await RefreshEnvironmentAsync();
        }
        else
        {
            TxtDeployState.Text = $"部署失败：{result.Error}";
            foreach (var line in result.Steps)
            {
                _deployLog.Add($"  · {line}");
            }
        }

        _deploying = false;
        BtnDeploy.IsEnabled = true;
    }
}
