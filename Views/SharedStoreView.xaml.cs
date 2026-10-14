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

    /// <summary>
    /// 界面「宿主类型」→ 官方 CLI 的 --host 取值。上游 ENTRY_HOSTS 只有
    /// hermes/codex/claude-code/workbuddy/dsh：<b>workbuddy-ai 是成员标识（--entry）而不是宿主</b>，
    /// 直接当 --host 传会被 argparse 判为非法取值并以 exit 2 打 usage。
    /// </summary>
    private static string CliHostOf(string uiTag) => uiTag == "workbuddy-ai" ? "workbuddy" : uiTag;

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

        var allEntries = (await _plugin.ListEntriesAsync(root)).ToList();
        // 与「助手连接」页同口径：detach 只是解除链接，记忆仍留在库里，但条目不再是
        // 「已接入的助手」。detach 只在 installation.json 里写 detached_at、不删行，
        // 所以必须显式过滤——不过滤就会出现「取消链接了控制台还显示已接入」。
        var entries = allEntries
            .Where(en => string.IsNullOrEmpty(en.DetachedAt))
            .ToList();
        var detachedCount = allEntries.Count - entries.Count;
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
            ? (detachedCount > 0
                ? $"这个共享库没有已接入的助手（{detachedCount} 个已取消链接的条目不再显示，其记忆仍保留在库中）。"
                : "这个共享库还没有接入任何助手，可在下方接入。")
            : $"已接入 {entries.Count} 个助手（记忆互通、权限各按原有设置）"
              + (detachedCount > 0 ? $"；另有 {detachedCount} 个已取消链接的条目不再统计。" : "。");
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

            var hostTag = PickerHost.SelectedTag ?? "";
            var cliHost = CliHostOf(hostTag);
            var entry = (TxtEntryId.Text ?? "").Trim();
            var displayName = (TxtDisplayName.Text ?? "").Trim();
            var instanceRoot = (TxtInstanceRoot.Text ?? "").Trim();

            if (!MaintenanceCli.EntryHosts.Contains(cliHost))
            {
                SetAttachState($"不支持的宿主类型：{hostTag}", "failed");
                return;
            }
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

            // 官方契约：attach 的 --root 是「共享库目录」本身（其下 installation.json 即清单），
            // 不是记忆库根；客户端宿主的授权必须参照**已接入的 Hermes 成员**（--grants-like /
            // --capture-like），Hermes 宿主反而不能给这两个开关。
            var sharedRoot = PathDiscovery.SharedDir(root);
            var grantsNote = "由本成员自带的安装清单推导（Hermes 宿主）";
            string? grantsLike = null;
            string? captureLike = null;
            if (MaintenanceCli.IsClientHost(cliHost))
            {
                var entriesNow = _plugin is null
                    ? new List<PluginEntry>()
                    : (await _plugin.ListEntriesAsync(root)).ToList();
                var hermesEntries = entriesNow
                    .Where(en => string.Equals(en.Host, "hermes", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (hermesEntries.Count == 0)
                {
                    SetAttachState(
                        "共享库里还没有 Hermes 成员：客户端成员的读写与捕获范围要参照 Hermes 成员的 owner 行，" +
                        "请先接入一个 Hermes 助手，再接入本成员。", "failed");
                    return;
                }
                grantsLike = "all";
                captureLike = hermesEntries[0].Entry;
                grantsNote = hermesEntries.Count == 1
                    ? $"参照 Hermes 成员「{captureLike}」"
                    : $"参照 Hermes 成员「{captureLike}」等 {hermesEntries.Count} 个（捕获范围取第一个）";
            }

            var runtimeConfigFrom = PathDiscovery.RuntimeConfigPath(root);
            var hasRoutes = File.Exists(runtimeConfigFrom);

            var dialog = new ContentDialog
            {
                Title = "确认接入共享库",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"将把「{displayName}」接入当前共享库：\n\n" +
                           $"• 宿主类型：{cliHost}\n" +
                           $"• 成员标识：{entry}\n" +
                           $"• 数据目录：{instanceRoot}\n" +
                           $"• 授权范围：{grantsNote}\n" +
                           $"• 向量路由：{(hasRoutes ? "沿用共享 worker 的模型（写入该成员自己的 runtime-config）" : "未找到 shared\\runtime-config.json，只能词法检索")}\n\n" +
                           "会写入共享库的 installation.json。若该助手此前已有自己的记忆库，请先把它移开" +
                           "（例如改名为 scope-recall.local-日期），否则官方 CLI 会拒绝接入；" +
                           "接入后该助手的新记忆都写入共享库。",
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
                MaintenanceCli.AttachArgs(
                    sharedRoot, cliHost, instanceRoot, entry, displayName,
                    grantsLike, captureLike, hasRoutes ? runtimeConfigFrom : null),
                timeoutMs: 600_000);

            if (result.Success)
            {
                var note = result.StdOut.Contains("vector_recall_unavailable", StringComparison.Ordinal)
                    ? "（提示：该成员未拿到向量路由，语义检索暂不可用，可为其补 runtime-config 后重接）"
                    : "";
                SetAttachState($"✓ 接入完成：「{displayName}」已加入共享库。{note}", "ok");
                await ReloadAsync();
            }
            else
            {
                SetAttachState($"✗ 接入失败：{MaintenanceCli.FailureText(result)}", "failed");
            }

            BtnAttach.IsEnabled = true;
        });

    private void SetAttachState(string message, string state)
    {
        TxtAttachState.Text = message;
        TxtAttachState.Foreground = StatusVisuals.BrushOf(state);
    }
}
