using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;
using Windows.Storage.Pickers;

namespace ScopeRecallWin.Views;

public sealed partial class InstallView : UserControl
{
    /// <summary>部署确认语句（勾选复选框即代表本人确认，等价于手写授权句）。</summary>
    private const string DeployConfirmSentence = "我确认在本机部署记忆插件";

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
        Loaded += (_, _) => UiThread.Run(DispatcherQueue, "InstallView.Loaded", LoadAsync);
    }

    private async Task LoadAsync()
    {
        await RefreshEnvironmentAsync();
        TxtTargetRoot.Text = PathDiscovery.FindMemoryRoot(App.Settings) ?? @"E:\ScopeRecall";
        UpdateRootState();
    }

    // ---------- 环境状态 ----------

    private async Task RefreshEnvironmentAsync()
    {
        if (_plugin is null)
        {
            ShowEnv("danger", "插件服务不可用", "内部服务初始化失败，请重启程序。");
            return;
        }

        try
        {
            var env = await _plugin.ProbeEnvironmentAsync();
            if (env.Root is null)
            {
                ShowEnv("pending", "尚未部署记忆插件", env.Error ?? "选择目录后点击下方「开始部署」即可。");
                return;
            }

            var version = env.InstalledVersion ?? "未安装";
            var parts = new System.Collections.Generic.List<string>
            {
                $"记忆库位置：{env.Root}",
                $"插件版本：{version}",
                env.ExternalUvAvailable ? "升级组件：已就绪" : "升级组件：缺失（升级时会提示）",
            };
            if (env.MemoryDbSize is { } size)
            {
                parts.Add($"记忆库大小：{Exporter.HumanSize(size)}");
            }

            ShowEnv(
                env.InstalledVersion is null ? "pending" : "ok",
                env.InstalledVersion is null ? "记忆库已存在，但插件版本未知" : $"已安装记忆插件 {version}",
                string.Join(" · ", parts));

            TxtEnvError.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.LogCrash("InstallView.RefreshEnvironment", ex);
            ShowEnv("danger", "环境检测失败", $"{ex.Message}（已记录日志，程序继续运行）");
        }
    }

    private void ShowEnv(string state, string headline, string detail)
    {
        var tint = state switch
        {
            "ok" => "ok",
            "pending" => "pending",
            _ => "failed",
        };
        EnvLed.Background = StatusVisuals.BrushOf(tint);
        TxtEnvHeadline.Text = headline;
        TxtEnvDetail.Text = detail;
    }

    private void ShowEnvError(string message)
    {
        TxtEnvError.Text = message;
        TxtEnvError.Visibility = Visibility.Visible;
    }

    private void BtnRefreshEnv_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "InstallView.RefreshEnv", RefreshEnvironmentAsync);

    // ---------- 目标目录 ----------

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "InstallView.Browse", async () =>
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
                TxtRootState.Foreground = StatusVisuals.BrushOf("failed");
            }
        });

    private void BtnUseDefault_Click(object sender, RoutedEventArgs e)
    {
        TxtTargetRoot.Text = @"E:\ScopeRecall";
        UpdateRootState();
    }

    private void BtnUseAppDir_Click(object sender, RoutedEventArgs e)
    {
        TxtTargetRoot.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "ScopeRecallWin", "data");
        UpdateRootState();
    }

    private void UpdateRootState()
    {
        try
        {
            var root = (TxtTargetRoot.Text ?? "").Trim();
            if (root.Length == 0)
            {
                TxtRootState.Text = "请选择部署目录。";
                TxtRootState.Foreground = (Brush)Application.Current.Resources["MutedTextBrush"];
                return;
            }

            if (PathDiscovery.IsMemoryRoot(root))
            {
                TxtRootState.Text = "✓ 该目录已有记忆库：将复用校验，不会重装，也不会删除任何数据。";
                TxtRootState.Foreground = StatusVisuals.BrushOf("ok");
            }
            else
            {
                var exists = Directory.Exists(root);
                var nonEmpty = exists && Directory.EnumerateFileSystemEntries(root).Any();
                TxtRootState.Text = nonEmpty
                    ? "该目录已存在且非空：将只新增记忆插件所需的子目录，不动其他文件。"
                    : "全新目录：将在这里创建记忆插件的运行环境与记忆库。";
                TxtRootState.Foreground = (Brush)Application.Current.Resources["MutedTextBrush"];
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("InstallView.UpdateRootState", ex);
            TxtRootState.Text = $"该目录不可访问：{ex.Message}";
            TxtRootState.Foreground = StatusVisuals.BrushOf("failed");
        }
    }

    // ---------- 部署计划 ----------

    private void BtnPlan_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "InstallView.Plan", async () =>
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
            TxtPlanWarning.Text = plan.Warning ?? "";
            TxtPlanWarning.Visibility = plan.Warning is null ? Visibility.Collapsed : Visibility.Visible;
        });

    // ---------- 执行部署 ----------

    private void BtnDeploy_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "InstallView.Deploy", async () =>
        {
            if (_plugin is null || _deploying)
            {
                return;
            }
            var root = (TxtTargetRoot.Text ?? "").Trim();
            if (root.Length == 0)
            {
                SetDeployState("请先选择部署目录。", "failed");
                return;
            }
            if (ChkConfirm.IsChecked != true)
            {
                SetDeployState("请先勾选确认框：需要你本人确认本次部署。", "pending");
                return;
            }

            _deploying = true;
            BtnDeploy.IsEnabled = false;
            _deployLog.Clear();
            DeployLogList.Visibility = Visibility.Visible;
            SetDeployState("部署进行中，请不要关闭程序…", "pending");

            var progress = new Progress<PluginInstallProgress>(p =>
            {
                _deployLog.Add($"[{p.Step}] {StateText(p.State)} {p.Detail}".Trim());
                DeployLogList.ScrollIntoView(_deployLog[^1]);
            });

            var result = await _plugin.ApplyInstallAsync(root, DeployConfirmSentence, progress);

            if (result.Success)
            {
                var version = result.InstalledVersion is null ? "" : $"（插件版本 {result.InstalledVersion}）";
                SetDeployState($"✓ 部署完成{version}。{result.Error ?? "目录权限检查通过，控制台与 Agent 都可以读写。"}", "ok");
                App.Settings.MemoryRootPath = root;
                App.Settings.Save();
                await RefreshEnvironmentAsync();
            }
            else
            {
                SetDeployState($"✗ 部署失败：{result.Error}", "failed");
                foreach (var line in result.Steps)
                {
                    _deployLog.Add("  · " + line);
                }
            }

            _deploying = false;
            BtnDeploy.IsEnabled = true;
        });

    private void SetDeployState(string message, string state)
    {
        TxtDeployState.Text = message;
        TxtDeployState.Foreground = StatusVisuals.BrushOf(state);
    }

    /// <summary>部署步骤状态中文名。</summary>
    private static string StateText(string? state) => state switch
    {
        "running" => "进行中…",
        "done" => "完成",
        "failed" => "失败",
        _ => state ?? "",
    };
}
