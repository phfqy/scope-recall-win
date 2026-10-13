using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScopeRecallWin.Controls;
using ScopeRecallWin.Helpers;
using ScopeRecallWin.Infrastructure;
using ScopeRecallWin.Models;
using ScopeRecallWin.Services;

namespace ScopeRecallWin.Views;

public sealed partial class ProfileView : UserControl
{
    private readonly IStorageService? _storage;
    private string? _dbPath;
    private List<ProfileGroupRow> _rows = new();
    private bool _loaded;

    public ProfileView()
    {
        InitializeComponent();
        _storage = ServiceLocator.Get<IStorageService>();
        Loaded += ProfileView_Loaded;
    }

    private void ProfileView_Loaded(object sender, RoutedEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "ProfileView.ProfileView_Loaded", async () =>
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            await ReloadAsync();

        });
    }

    private string? ResolveDbPath()
    {
        var root = PathDiscovery.FindMemoryRoot(App.Settings);
        if (root is null)
        {
            return null;
        }
        var path = PathDiscovery.MemoryDbPath(root);
        return File.Exists(path) ? path : null;
    }

    private async Task ReloadAsync()
    {
        _dbPath = ResolveDbPath();
        if (_dbPath is null || _storage is null)
        {
            TxtTotal.Text = "未找到记忆库：请先在「安装」页选择部署目录。";
            GroupList.ItemsSource = null;
            return;
        }

        _rows = (await _storage.GetProfileGroupsAsync(_dbPath, 300)).ToList();

        var vms = _rows.Select(g => new
        {
            Subject = g.Subject,
            CountText = $"{g.ClaimCount} 条 · active {g.ActiveCount}",
            NewestText = g.NewestFrom is null ? "" : $"更新 {Helpers.TimestampNormalizer.Parse(g.NewestFrom).ToLocalTime():yyyy-MM-dd}",
        }).ToList();

        TxtTotal.Text = $"共 {_rows.Count} 个主题（按 claim 数排序，最多 300）";
        GroupList.ItemsSource = vms;
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => UiThread.Run(DispatcherQueue, "ProfileView.BtnRefresh_Click", ReloadAsync);

    private void GroupList_ItemClick(object sender, ItemClickEventArgs e)
    {
        UiThread.Run(DispatcherQueue, "ProfileView.GroupList_ItemClick", async () =>
        {
            var index = GroupList.Items.IndexOf(e.ClickedItem);
            if (index < 0 || index >= _rows.Count || _dbPath is null || _storage is null)
            {
                return;
            }
            var group = _rows[index];

            // 抽屉内展示该 subject 下的 claim 列表（复用实体检索）
            var claims = (await _storage.EntitySearchAsync(_dbPath, group.Subject, 100)).ToList();

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"{group.Subject} · {group.ClaimCount} 条（active {group.ActiveCount}）",
                Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
            });
            foreach (var c in claims)
            {
                var box = new Border
                {
                    Background = (Brush)Application.Current.Resources["Surface2Brush"],
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10),
                };
                box.Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{c.Predicate} · {c.State}",
                            FontSize = 12.5,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = $"{c.ClaimId} · kind {c.Kind} · rev {c.CurrentRevision}",
                            FontSize = 11,
                            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                        },
                    },
                };
                panel.Children.Add(box);
            }

            Drawer.Open($"主题 {group.Subject}", panel);

        });
    }
}
