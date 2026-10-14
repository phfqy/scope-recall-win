using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScopeRecallWin.Infrastructure;
using Windows.System;

namespace ScopeRecallWin.Views;

public sealed partial class SettingsView : UserControl
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ScopeRecallWin";
    private bool _loaded;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;

        // 外观主题
        switch (ThemeService.Normalize(App.Settings.Theme))
        {
            case ThemeService.Light: RadioThemeLight.IsChecked = true; break;
            case ThemeService.Dark: RadioThemeDark.IsChecked = true; break;
            default: RadioThemeSystem.IsChecked = true; break;
        }
        RefreshThemeState();

        // 更新模式
        RadioAuto.IsChecked = App.Settings.UpdateMode != "notify";
        RadioNotify.IsChecked = App.Settings.UpdateMode == "notify";
        var channel = App.Settings.UpdateChannel == "mirror" ? "镜像（gh-proxy）" : "GitHub 直连";
        TxtUpdateChannel.Text = $"更新源：{channel}（检查时自动回退备用通道）。";

        // 开机自启：读注册表 Run key 现状
        ToggleStartup.IsOn = IsStartupEnabled();
        TxtStartupState.Text = ToggleStartup.IsOn
            ? "已开启：登录时自动启动。"
            : "已关闭：不随系统登录启动。";

        // 记忆库位置
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        TxtMemoryRoot.Text = root ?? "未检测到记忆库（可在「安装」页选择部署目录）";
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        var setting = RadioThemeLight.IsChecked == true ? ThemeService.Light
            : RadioThemeDark.IsChecked == true ? ThemeService.Dark
            : ThemeService.System;
        App.Settings.Theme = setting;
        App.Settings.Save();
        ThemeService.Apply(setting);
        RefreshThemeState();
    }

    private void RefreshThemeState()
    {
        if (TxtThemeState is null)
        {
            return;
        }
        var setting = ThemeService.Normalize(App.Settings.Theme);
        TxtThemeState.Text = setting == ThemeService.System
            ? $"当前：跟随系统（实际为{(ThemeService.IsDark ? "深色" : "浅色")}）。"
            : $"当前：{ThemeService.DisplayName(setting)}。";
    }

    private void UpdateMode_Changed(object sender, RoutedEventArgs e)
    {
        App.Settings.UpdateMode = RadioNotify.IsChecked == true ? "notify" : "auto";
        App.Settings.Save();
    }

    private void ToggleStartup_Toggled(object sender, RoutedEventArgs e)
    {
        var ok = SetStartupEnabled(ToggleStartup.IsOn);
        TxtStartupState.Text = ToggleStartup.IsOn
            ? (ok ? "已开启：登录时自动启动。" : "开启失败：注册表不可写。")
            : (ok ? "已关闭。" : "关闭失败：注册表不可写。");
    }

    private void BtnOpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = AppSettings.SettingsDir,
                UseShellExecute = false,
            });
        }
        catch
        {
            TxtStartupState.Text = $"无法打开资源管理器，设置目录：{AppSettings.SettingsDir}";
        }
    }

    // ---------- 注册表 Run key（HKCU，免管理员） ----------

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var value = key?.GetValue(RunValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    private static bool SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key is null)
            {
                return false;
            }
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "ScopeRecallWin.exe";
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
