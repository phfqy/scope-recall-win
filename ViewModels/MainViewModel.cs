using CommunityToolkit.Mvvm.ComponentModel;

namespace ScopeRecallWin.ViewModels;

/// <summary>
/// 应用级状态。骨架阶段承载主题与顶栏信息；P1 扩展：自动刷新计时、巡检计划、
/// 审计订阅状态、⌘K 候选数据源。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _pageTitle = "总览";

    [ObservableProperty]
    private string _pageDescription = "存储、规模、分布与趋势";

    [ObservableProperty]
    private string _updatedAt = "--:--:--";

    [ObservableProperty]
    private bool _autoRefresh = true;

    [ObservableProperty]
    private int _refreshIntervalSeconds = 10;
}
