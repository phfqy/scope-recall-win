using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class SharedStoreView : UserControl
{
    /// <summary>宿主类型 → 默认数据目录（选中即自动填写，可改）。</summary>
    private static readonly Dictionary<string, (string Dir, string Label, string Sample)> HostDefaults = new()
    {
        ["workbuddy"] = (".workbuddy", "WorkBuddy", "workbuddy"),
        ["workbuddy-ai"] = (".workbuddy-ai", "WorkBuddy AI", "workbuddy-ai"),
        ["dsh"] = (".dsh", "DeepSeek Harness", "dsh"),
        ["hermes"] = ("", "Hermes", "hermes"),
        ["codex"] = (".codex", "Codex", "codex"),
        ["claude-code"] = (".claude", "Claude Code", "claude-code"),
    };

    private readonly IPluginService? _plugin;

    public SharedStoreView()
    {
        InitializeComponent();
        _plugin = ServiceLocator.Get<IPluginService>();
        // 宿主类型：选择后自动填充成员标识、显示名称与默认数据目录
        PickerHost.SetItems(
            new[] { "WorkBuddy", "WorkBuddy AI", "DeepSeek Harness（DSH）", "Hermes", "Codex", "Claude Code" },
            selectedIndex: 0,
            tags: new[] { "workbuddy", "workbuddy-ai", "dsh", "hermes", "codex", "claude-code" });
        PickerHost.SelectionChanged += (_, _) => ApplyHostPreset();
        Loaded += (_, _) => UiThread.Run(DispatcherQueue, "SharedStoreView.Loaded", LoadAsync);
    }

    private async Task LoadAsync()
    {
        await ReloadAsync();
        ApplyHostPreset();
    }

    // ---------- 当前共享库 ----------

    private async Task ReloadAsync()
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        TxtRoot.Text = root is null ? "未找到共享库" : $"位置：{root}";
        StoreLed.Background = root is null
            ? StatusVisuals.BrushOf("failed")
            : StatusVisuals.BrushOf("ok");

        if (root is null || _plugin is null)
        {
            TxtState.Text = "请先到「安装」页选择部署目录并完成部署。";
            TxtState.Foreground = StatusVisuals.BrushOf("failed");
            EntryList.ItemsSource = null;
            return;
        }

        var entries = (await _plugin.ListEntriesAsync(root)).ToList();
        EntryList.ItemsSource = entries.Select(en =>
        {
            var name = string.IsNullOrWhiteSpace(en.DisplayName) ? en.Entry : $"{en.DisplayName}（{en.Entry}）";
            var parts = new List<string> { $"类型：{en.Host}" };
            if (en.Home is { Length: > 0 })
            {
                // 注意：PluginEntry.Alive 表示 resident 常驻服务是否在跑，与目录存在与否是两回事
                var homeExists = Directory.Exists(en.Home);
                parts.Add($"数据目录：{en.Home}（{(homeExists ? "存在" : "不存在")}）");
            }
            parts.Add(en.Alive ? "常驻服务：运行中" : "常驻服务：未运行（首次使用时自动启动）");
            if (en.Python is { Length: > 0 })
            {
                parts.Add("使用共享运行环境");
            }
            return new { Title = name, Meta = string.Join(" · ", parts) };
        }).ToList();

        TxtState.Text = entries.Count == 0
            ? "这个共享库还没有接入任何助手，可在下方接入。"
            : $"已接入 {entries.Count} 个助手（记忆互通、权限各按原有设置）。";
        TxtState.Foreground = StatusVisuals.BrushOf(entries.Count == 0 ? "pending" : "ok");
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "SharedStoreView.Refresh", ReloadAsync);

    // ---------- 表单 ----------

    private void ApplyHostPreset()
    {
        var tag = PickerHost.SelectedTag ?? "";
        if (!HostDefaults.TryGetValue(tag, out var preset))
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        TxtEntryId.Text = preset.Sample;
        TxtDisplayName.Text = preset.Label;
        TxtInstanceRoot.Text = preset.Dir.Length == 0
            ? ""      // Hermes 的数据目录因安装方式而异，留空由用户填写
            : System.IO.Path.Combine(home, preset.Dir);
        UpdateAttachHint(tag);
    }

    private void UpdateAttachHint(string hostTag)
    {
        TxtAttachHint.Text = hostTag switch
        {
            "workbuddy" => "提示：WorkBuddy 的数据目录通常是 C:\\Users\\<用户名>\\.workbuddy",
            "workbuddy-ai" => "提示：WorkBuddy AI 的数据目录通常是 C:\\Users\\<用户名>\\.workbuddy-ai",
            "dsh" => "提示：DeepSeek Harness 的数据目录通常是 C:\\Users\\<用户名>\\.dsh",
            "hermes" => "提示：Hermes 的数据目录取决于安装方式（请在 Hermes 设置中确认后填写）",
            "codex" => "提示：Codex 的数据目录通常是 C:\\Users\\<用户名>\\.codex",
            _ => "提示：Claude Code 的数据目录通常是 C:\\Users\\<用户名>\\.claude",
        };
    }

    // ---------- 接入（写操作：CLI + 勾选确认） ----------

    private void BtnAttach_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "SharedStoreView.Attach", async () =>
        {
            var root = PathDiscovery.FindMemoryRoot(App.Settings);
            if (root is null)
            {
                SetAttachState("未找到共享库，请先到「安装」页完成部署。", "failed");
                return;
            }
            var venvPython = PathDiscovery.FindVenvPython(root);
            if (venvPython is null)
            {
                SetAttachState("未找到共享运行环境（shared-venv），无法执行接入。", "failed");
                return;
            }

            var host = PickerHost.SelectedTag ?? "";
            var entry = (TxtEntryId.Text ?? "").Trim();
            var displayName = (TxtDisplayName.Text ?? "").Trim();
            var instanceRoot = (TxtInstanceRoot.Text ?? "").Trim();

            if (!Regex.IsMatch(entry, "^[a-z0-9-]{2,32}$"))
            {
                SetAttachState("成员标识需为 2~32 位小写字母、数字或连字符。", "failed");
                return;
            }
            if (displayName.Length is < 1 or > 32)
            {
                SetAttachState("显示名称需为 1~32 个字符。", "failed");
                return;
            }
            if (instanceRoot.Length == 0)
            {
                SetAttachState("请填写该助手的数据目录。", "failed");
                return;
            }
            if (!Directory.Exists(instanceRoot))
            {
                SetAttachState($"该数据目录不存在：{instanceRoot}（请确认路径是否正确）", "failed");
                return;
            }
            var dialog = new ContentDialog
            {
                Title = "确认接入共享库",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"将把「{displayName}」接入当前共享库：\n\n" +
                           $"• 成员标识：{entry}\n" +
                           $"• 数据目录：{instanceRoot}\n\n" +
                           "它原有的记忆会并入共享库（原库会自动备份），之后新记忆也写入共享库。",
                },
                PrimaryButtonText = "确认接入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            BtnAttach.IsEnabled = false;
            SetAttachState("正在接入，请稍候…", "pending");

            var result = await ProcessRunner.RunAsync(
                venvPython,
                $"-m scope_recall.maintenance.cli attach --host {host} --instance-root \"{instanceRoot}\" " +
                $"--root \"{root}\" --entry {entry} --display-name \"{displayName}\"",
                timeoutMs: 600_000);

            if (result.Success)
            {
                SetAttachState($"✓ 接入完成：「{displayName}」已加入共享库。", "ok");
                await ReloadAsync();
            }
            else
            {
                var reason = FirstLine(result.StdErr) ?? FirstLine(result.StdOut) ?? $"退出码 {result.ExitCode}";
                SetAttachState($"✗ 接入失败：{reason}", "failed");
            }

            BtnAttach.IsEnabled = true;
        });

    private void SetAttachState(string message, string state)
    {
        TxtAttachState.Text = message;
        TxtAttachState.Foreground = StatusVisuals.BrushOf(state);
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
}
