using System;
using Microsoft.UI.Xaml;

namespace ScopeRecallWin.Infrastructure;

/// <summary>
/// 主题（外观）服务：跟随系统 / 浅色 / 深色。
///
/// 说明：应用的深浅色由窗口根元素的 RequestedTheme 决定；标题栏由系统绘制，
/// 若不显式设置会**跟随系统主题**而不是应用主题 —— 这正是"顶栏色调与内容区不统一"的原因。
/// 因此这里在切换主题时同时设置标题栏（背景/前景/按钮/悬停）颜色，保证上下一致。
/// </summary>
public static class ThemeService
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>主题变化通知（视图可据此重绘自绘元素）。</summary>
    public static event Action? Changed;

    /// <summary>当前生效的主题设置值。</summary>
    public static string CurrentSetting { get; private set; } = System;

    /// <summary>当前实际生效的明暗（用于自绘元素选色）。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>规范化主题设置值。</summary>
    public static string Normalize(string? setting)
        => setting switch
        {
            Light => Light,
            Dark => Dark,
            _ => System,
        };

    /// <summary>应用主题设置（幂等；可在启动时与设置页切换时调用）。</summary>
    public static void Apply(string? setting)
    {
        var normalized = Normalize(setting);
        CurrentSetting = normalized;

        var theme = normalized switch
        {
            Light => ElementTheme.Light,
            Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        IsDark = normalized switch
        {
            Dark => true,
            Light => false,
            _ => SystemPrefersDark(),
        };

        App.MainWindow?.ApplyTheme(theme);
        Changed?.Invoke();
    }

    /// <summary>读取系统"应用模式"偏好（跟随系统时使用）。</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light)
            {
                return light == 0;
            }
        }
        catch
        {
            // 注册表不可读时按深色处理（应用主色调为深色）
        }
        return true;
    }

    /// <summary>主题设置的中文名。</summary>
    public static string DisplayName(string setting) => Normalize(setting) switch
    {
        Light => "浅色",
        Dark => "深色",
        _ => "跟随系统",
    };
}
