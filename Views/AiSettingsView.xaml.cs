using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class AiSettingsView : UserControl
{
    private readonly IModelConfigService? _models;
    private ModelConfig? _current;
    private bool _loaded;

    public AiSettingsView()
    {
        InitializeComponent();
        _models = ServiceLocator.Get<IModelConfigService>();
        Loaded += AiSettingsView_Loaded;
    }

    private async void AiSettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await LoadAsync();
    }

    private async void BtnReload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_models is null)
        {
            TxtConfigPath.Text = "模型配置服务不可用。";
            return;
        }
        _current = await _models.LoadAsync();
        if (_current is null)
        {
            TxtConfigPath.Text = "未找到 runtime-config.json：请先在「安装」页选择部署目录。";
            return;
        }

        TxtConfigPath.Text = _current.ConfigPath;
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

        await RefreshKeyStateAsync();
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
            ? $"凭据管理器已存有「{env}」的 Key（值不回显，仅显示状态）。"
            : $"凭据管理器中没有「{env}」的 Key。运行时将回退读取 shared\\.env。";
    }

    private async void BtnTestEmbedding_Click(object sender, RoutedEventArgs e)
        => await TestAsync(TxtEmbeddingEndpoint.Text, TxtEmbeddingCredentialEnv.Text, TxtEmbeddingTest);

    private async void BtnTestConsolidation_Click(object sender, RoutedEventArgs e)
    {
        var env = (TxtEmbeddingCredentialEnv.Text ?? "").Trim();
        await TestAsync(TxtConsolidationEndpoint.Text, env, TxtConsolidationTest);
    }

    private async Task TestAsync(string? endpoint, string? credentialEnv, TextBlock target)
    {
        if (_models is null)
        {
            return;
        }
        target.Text = "正在测试连接…";
        var result = await _models.TestConnectionAsync(endpoint ?? "", (credentialEnv ?? "").Trim());
        var icon = result.Ok ? "✓" : "✗";
        target.Text = $"{icon} {result.Detail}（{result.LatencyMs}ms）" +
                      (result.NeedsKey ? " · 建议先在下方保存 API Key" : "");
    }

    private async void BtnSaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (_models is null)
        {
            return;
        }
        var env = (TxtEmbeddingCredentialEnv.Text ?? "").Trim();
        var secret = PwdApiKey.Password ?? "";
        if (env.Length == 0)
        {
            TxtKeyState.Text = "请先填写 credential_env（环境变量名）。";
            return;
        }
        if (secret.Trim().Length < 8)
        {
            TxtKeyState.Text = "Key 过短，未保存。";
            return;
        }
        var ok = await _models.StoreKeyAsync(env, secret.Trim());
        PwdApiKey.Password = "";
        TxtKeyState.Text = ok
            ? $"已保存到 Windows 凭据管理器（{env}）。值不回显、不落配置文件。"
            : "保存失败：凭据管理器不可写。";
    }

    private async void BtnDeleteKey_Click(object sender, RoutedEventArgs e)
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
            Title = "删除已存 Key",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"将从 Windows 凭据管理器删除「{env}」的 Key。删除后运行时将回退读取 shared\\.env。",
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
        TxtKeyState.Text = ok ? $"已删除「{env}」的 Key。" : "删除失败（可能本来就不存在）。";
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_models is null || _current is null)
        {
            return;
        }

        if (!int.TryParse((TxtEmbeddingDimensions.Text ?? "").Trim(), out var dims) || dims < 1)
        {
            TxtSaveState.Text = "dimensions 必须是正整数。";
            return;
        }
        if (!double.TryParse((TxtVectorThreshold.Text ?? "").Trim(), out var threshold)
            || threshold < 0 || threshold > 1)
        {
            TxtSaveState.Text = "vector_threshold 必须是 0~1 之间的数。";
            return;
        }
        int? maxTokens = null;
        var rawTokens = (TxtMaxOutputTokens.Text ?? "").Trim();
        if (rawTokens.Length > 0)
        {
            if (!int.TryParse(rawTokens, out var parsed))
            {
                TxtSaveState.Text = "max_output_tokens 必须是整数。";
                return;
            }
            maxTokens = parsed;
        }

        var auth = (TxtAuthorization.Text ?? "").Trim();
        if (auth.Length < 6)
        {
            TxtSaveState.Text = "授权句不足 6 字：请用一句话确认你本人了解本次配置修改。";
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

        TxtSaveState.Text = "正在保存（含备份）…";
        var result = await _models.SaveAsync(updated, auth);
        if (result.Ok)
        {
            TxtSaveState.Text = $"已保存。原配置备份：{result.BackupPath ?? "（未生成）"}";
            await LoadAsync();
        }
        else
        {
            TxtSaveState.Text = $"保存失败：{result.Error}（配置未被修改）";
        }
    }
}
