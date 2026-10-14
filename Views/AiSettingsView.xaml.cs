using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class AiSettingsView : UserControl
{
    /// <summary>服务商预设：一次选择即填好接口地址与技术参数。</summary>
    private sealed record ProviderPreset(
        string EmbeddingEndpoint, string EmbeddingModel, int Dimensions, string CredentialEnv,
        string ConsolidationEndpoint, string ConsolidationModel, int MaxOutputTokens);

    private static readonly ProviderPreset Zhipu = new(
        "https://open.bigmodel.cn/api/paas/v4/embeddings", "embedding-3", 2048, "ZHIPU_API_KEY",
        "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-4-flash-250414", 8192);

    private static readonly ProviderPreset OpenAi = new(
        "https://api.openai.com/v1/embeddings", "text-embedding-3-small", 1536, "OPENAI_API_KEY",
        "https://api.openai.com/v1/chat/completions", "gpt-4o-mini", 4096);

    private readonly IModelConfigService? _models;
    private ModelConfig? _current;
    private bool _loading;

    public AiSettingsView()
    {
        InitializeComponent();
        _models = ServiceLocator.Get<IModelConfigService>();
        PickerProvider.SetItems(
            new[] { "智谱 AI（推荐，本机当前使用）", "OpenAI（或兼容接口）", "自定义（手动填写接口地址）" },
            selectedIndex: 0,
            tags: new[] { "zhipu", "openai", "custom" });
        PickerProvider.SelectionChanged += (_, _) => ApplyProviderPreset();
        Loaded += (_, _) => UiThread.Run(DispatcherQueue, "AiSettingsView.Loaded", LoadAsync);
    }

    private void BtnReload_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "AiSettingsView.Reload", LoadAsync);

    // ---------- 读取 ----------

    private async Task LoadAsync()
    {
        if (_models is null)
        {
            TxtConfigPath.Text = "模型配置服务不可用。";
            return;
        }

        _loading = true;
        _current = await _models.LoadAsync();
        if (_current is null)
        {
            TxtConfigPath.Text = "未找到配置文件（runtime-config.json）：请先在「安装」页完成部署。";
            _loading = false;
            return;
        }

        TxtConfigPath.Text = "配置文件：" + _current.ConfigPath;

        // 高级模式：全字段
        TxtEmbeddingModel.Text = _current.EmbeddingModel;
        TxtEmbeddingEndpoint.Text = _current.EmbeddingEndpoint;
        TxtEmbeddingDimensions.Text = _current.EmbeddingDimensions.ToString();
        TxtEmbeddingCredentialEnv.Text = _current.EmbeddingCredentialEnv;
        TglExternalEmbedding.IsOn = _current.ExternalEmbedding;

        TxtConsolidationModel.Text = _current.ConsolidationModel;
        TxtConsolidationEndpoint.Text = _current.ConsolidationEndpoint;
        TxtConsolidationKind.Text = _current.ConsolidationKind;
        TxtMaxOutputTokens.Text = _current.MaxOutputTokens?.ToString() ?? "";
        TglExternalConsolidation.IsOn = _current.ExternalConsolidation;

        TxtVectorThreshold.Text = _current.VectorThreshold.ToString("0.##");

        // 简单模式：服务商推断 + 模型名
        var provider = InferProvider(_current.EmbeddingEndpoint);
        PickerProvider.SelectByTag(provider);
        TxtSimpleModel.Text = _current.EmbeddingModel;
        UpdateEndpointInfo(provider);

        await RefreshKeyStateAsync();
        _loading = false;
    }

    private static string InferProvider(string? endpoint)
    {
        if (endpoint is null)
        {
            return "custom";
        }
        if (endpoint.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase))
        {
            return "zhipu";
        }
        if (endpoint.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
        {
            return "openai";
        }
        return "custom";
    }

    private void UpdateEndpointInfo(string provider)
    {
        TxtSimpleEndpointInfo.Text = provider switch
        {
            "zhipu" => "接口地址已自动填写：https://open.bigmodel.cn/api/paas/v4/（智谱 AI）",
            "openai" => "接口地址已自动填写：https://api.openai.com/v1/（OpenAI）",
            _ => "自定义模式：接口地址请在下方「高级设置」中填写。",
        };
    }

    private async Task RefreshKeyStateAsync()
    {
        if (_models is null)
        {
            return;
        }
        var env = (TxtEmbeddingCredentialEnv.Text ?? "").Trim();
        var has = env.Length > 0 && await _models.HasStoredKeyAsync(env);
        TxtKeyState.Text = has
            ? $"已保存 Key（存储名：{env}，值不回显）。重新填写即可覆盖。"
            : $"尚未保存 Key（存储名：{env}）。填好后点上方按钮即可保存。";
    }

    // ---------- 简单模式 ----------

    /// <summary>选择服务商后自动填好接口地址与常用参数。</summary>
    private void ApplyProviderPreset()
    {
        if (_loading)
        {
            return;
        }
        var tag = PickerProvider.SelectedTag ?? "custom";
        var preset = tag switch { "zhipu" => Zhipu, "openai" => OpenAi, _ => null };
        if (preset is not null)
        {
            TxtSimpleModel.Text = preset.EmbeddingModel;
            // 同步到高级字段（保存时以高级字段为准）
            TxtEmbeddingEndpoint.Text = preset.EmbeddingEndpoint;
            TxtEmbeddingDimensions.Text = preset.Dimensions.ToString();
            TxtEmbeddingCredentialEnv.Text = preset.CredentialEnv;
            TxtConsolidationEndpoint.Text = preset.ConsolidationEndpoint;
            TxtConsolidationModel.Text = preset.ConsolidationModel;
            TxtConsolidationKind.Text = "openai";
            TxtMaxOutputTokens.Text = preset.MaxOutputTokens.ToString();
            UiThread.Run(DispatcherQueue, "AiSettingsView.RefreshKeyState", RefreshKeyStateAsync);
        }
        UpdateEndpointInfo(tag);
    }

    private void BtnSimpleTest_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "AiSettingsView.SimpleTest", async () =>
        {
            if (_models is null)
            {
                return;
            }
            TxtSimpleResult.Text = "正在测试连接…";
            TxtSimpleResult.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"];

            var endpoint = (TxtEmbeddingEndpoint.Text ?? "").Trim();
            var env = (TxtEmbeddingCredentialEnv.Text ?? "").Trim();
            var result = await _models.TestConnectionAsync(endpoint, env);

            var label = result.Ok ? "✓ 连接正常" : "✗ 连接失败";
            TxtSimpleResult.Text = $"{label}：{result.Detail}（耗时 {result.LatencyMs} 毫秒）" +
                                   (result.NeedsKey ? "。提示：还没有保存 API Key" : "");
            TxtSimpleResult.Foreground = StatusVisuals.BrushOf(result.Ok ? "ok" : "failed");
        });

    private void BtnSimpleSave_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "AiSettingsView.SimpleSave", async () =>
        {
            if (_models is null || _current is null)
            {
                TxtSimpleResult.Text = "未读取到现有配置，无法保存。";
                return;
            }
            if (!await Confirm.AskAsync(
                    this,
                    "保存模型配置",
                    "将把当前填写的服务商、模型与接口地址写入记忆配置，并保存 API Key。",
                    "保存前会自动备份原配置文件；若填写的接口或密钥有误，记忆的向量化与整合会失败。"))
            {
                return;
            }

            // 1) 若有填 Key，先存入凭据管理器
            var env = (TxtEmbeddingCredentialEnv.Text ?? "").Trim();
            var key = PwdApiKey.Password ?? "";
            if (env.Length > 0 && key.Trim().Length >= 8)
            {
                var stored = await _models.StoreKeyAsync(env, key.Trim());
                PwdApiKey.Password = "";
                if (!stored)
                {
                    TxtSimpleResult.Text = "API Key 保存失败（凭据管理器不可写），配置未修改。";
                    TxtSimpleResult.Foreground = StatusVisuals.BrushOf("failed");
                    return;
                }
            }

            // 2) 保存配置（以高级字段为准，简单模式的模型名同步进去）
            if (!int.TryParse((TxtEmbeddingDimensions.Text ?? "").Trim(), out var dims) || dims < 1)
            {
                TxtSimpleResult.Text = "向量维度不是有效数字，请检查「高级设置」。";
                TxtSimpleResult.Foreground = StatusVisuals.BrushOf("failed");
                return;
            }

            var updated = _current with
            {
                EmbeddingModel = (TxtSimpleModel.Text ?? "").Trim(),
                EmbeddingEndpoint = (TxtEmbeddingEndpoint.Text ?? "").Trim(),
                EmbeddingCredentialEnv = env,
                EmbeddingDimensions = dims,
                ConsolidationModel = (TxtConsolidationModel.Text ?? "").Trim(),
                ConsolidationEndpoint = (TxtConsolidationEndpoint.Text ?? "").Trim(),
                ConsolidationKind = string.IsNullOrWhiteSpace(TxtConsolidationKind.Text)
                    ? "openai" : TxtConsolidationKind.Text.Trim(),
                MaxOutputTokens = int.TryParse((TxtMaxOutputTokens.Text ?? "").Trim(), out var mt) ? mt : _current.MaxOutputTokens,
            };

            var save = await _models.SaveAsync(updated);
            if (!save.Ok)
            {
                TxtSimpleResult.Text = $"保存失败：{save.Error}（原配置未被修改）";
                TxtSimpleResult.Foreground = StatusVisuals.BrushOf("failed");
                return;
            }

            // 3) 保存后自动测试连接
            var test = await _models.TestConnectionAsync(updated.EmbeddingEndpoint, updated.EmbeddingCredentialEnv);
            TxtSimpleResult.Text = test.Ok
                ? $"✓ 已保存并连接正常（耗时 {test.LatencyMs} 毫秒）。原配置备份：{save.BackupPath}"
                : $"✓ 配置已保存。但连接测试未通过：{test.Detail}（耗时 {test.LatencyMs} 毫秒）";
            TxtSimpleResult.Foreground = StatusVisuals.BrushOf(test.Ok ? "ok" : "pending");
            await LoadAsync();
        });

    // ---------- 高级模式 ----------

    private void BtnSave_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "AiSettingsView.SaveAdv", async () =>
        {
            if (_models is null || _current is null)
            {
                return;
            }
            if (!int.TryParse((TxtEmbeddingDimensions.Text ?? "").Trim(), out var dims) || dims < 1)
            {
                TxtSaveState.Text = "向量维度必须是正整数。";
                TxtSaveState.Foreground = StatusVisuals.BrushOf("failed");
                return;
            }
            if (!double.TryParse((TxtVectorThreshold.Text ?? "").Trim(), out var threshold)
                || threshold < 0 || threshold > 1)
            {
                TxtSaveState.Text = "相似度阈值必须是 0~1 之间的数。";
                TxtSaveState.Foreground = StatusVisuals.BrushOf("failed");
                return;
            }
            int? maxTokens = null;
            var rawTokens = (TxtMaxOutputTokens.Text ?? "").Trim();
            if (rawTokens.Length > 0)
            {
                if (!int.TryParse(rawTokens, out var parsed))
                {
                    TxtSaveState.Text = "单次最大输出长度必须是整数。";
                    TxtSaveState.Foreground = StatusVisuals.BrushOf("failed");
                    return;
                }
                maxTokens = parsed;
            }

            if (!await Confirm.AskAsync(
                    this,
                    "保存高级配置",
                    "将写入嵌入模型、整合模型与检索阈值等技术参数。",
                    "参数错误会导致记忆向量化或整合失败；保存前会自动备份原配置。"))
            {
                return;
            }

            var updated = _current with
            {
                EmbeddingModel = (TxtEmbeddingModel.Text ?? "").Trim(),
                EmbeddingEndpoint = (TxtEmbeddingEndpoint.Text ?? "").Trim(),
                EmbeddingCredentialEnv = (TxtEmbeddingCredentialEnv.Text ?? "").Trim(),
                EmbeddingDimensions = dims,
                ConsolidationModel = (TxtConsolidationModel.Text ?? "").Trim(),
                ConsolidationEndpoint = (TxtConsolidationEndpoint.Text ?? "").Trim(),
                ConsolidationKind = (TxtConsolidationKind.Text ?? "").Trim(),
                MaxOutputTokens = maxTokens,
                ExternalEmbedding = TglExternalEmbedding.IsOn,
                ExternalConsolidation = TglExternalConsolidation.IsOn,
                VectorThreshold = threshold,
            };

            TxtSaveState.Text = "正在保存（含自动备份）…";
            var result = await _models.SaveAsync(updated);
            if (result.Ok)
            {
                TxtSaveState.Text = $"✓ 已保存。原配置备份：{result.BackupPath ?? "（未生成）"}";
                TxtSaveState.Foreground = StatusVisuals.BrushOf("ok");
                await LoadAsync();
            }
            else
            {
                TxtSaveState.Text = $"✗ 保存失败：{result.Error}（配置未被修改）";
                TxtSaveState.Foreground = StatusVisuals.BrushOf("failed");
            }
        });

    private void BtnTestEmbedding_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "AiSettingsView.TestEmbedding",
            () => TestAsync(TxtEmbeddingEndpoint.Text, TxtEmbeddingCredentialEnv.Text, TxtEmbeddingTest));

    private void BtnTestConsolidation_Click(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "AiSettingsView.TestConsolidation", async () =>
        {
            var env = (TxtEmbeddingCredentialEnv.Text ?? "").Trim();
            await TestAsync(TxtConsolidationEndpoint.Text, env, TxtConsolidationTest);
        });
    }

    private async Task TestAsync(string? endpoint, string? credentialEnv, TextBlock target)
    {
        if (_models is null)
        {
            return;
        }
        target.Text = "正在测试连接…";
        target.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"];

        var result = await _models.TestConnectionAsync(endpoint ?? "", (credentialEnv ?? "").Trim());
        target.Text = (result.Ok ? "✓ 连接正常" : "✗ 连接失败") +
                      $"：{result.Detail}（耗时 {result.LatencyMs} 毫秒）" +
                      (result.NeedsKey ? "。提示：还没有保存 API Key" : "");
        target.Foreground = StatusVisuals.BrushOf(result.Ok ? "ok" : "failed");
    }

    // ---------- 凭据管理 ----------

    private void BtnDeleteKey_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "AiSettingsView.DeleteKey", async () =>
        {
            if (_models is null)
            {
                return;
            }
            var env = (TxtEmbeddingCredentialEnv.Text ?? "").Trim();
            if (env.Length == 0)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "删除已保存的 Key",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"将从 Windows 凭据管理器删除「{env}」的 Key。\n" +
                           "删除后运行时将回退读取 shared 目录下的 .env 文件。",
                },
                PrimaryButtonText = "确认删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var ok = await _models.DeleteKeyAsync(env);
            TxtKeyState.Text = ok ? $"已删除「{env}」的 Key。" : "删除失败（可能本来就没有保存）。";
            TxtKeyState.Foreground = StatusVisuals.BrushOf(ok ? "ok" : "failed");
        });
}
