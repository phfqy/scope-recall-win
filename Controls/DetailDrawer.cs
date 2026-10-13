using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ScopeRecallWin.Controls;

/// <summary>
/// 右侧滑出详情抽屉：覆盖宿主右半区，含标题栏 + 内容区 + 关闭按钮。
/// 用法：view.Drawer.Open(title, contentElement)；内容区由调用方构造。
/// </summary>
public sealed partial class DetailDrawer : UserControl
{
    private Border? _panel;
    private TextBlock? _title;
    private ContentControl? _body;

    public DetailDrawer()
    {
        BuildUi();
    }

    public bool IsOpen { get; private set; }

    /// <summary>打开抽屉（title 标题，body 内容元素）。</summary>
    public void Open(string title, UIElement body)
    {
        if (_title is not null)
        {
            _title.Text = title;
        }
        if (_body is not null)
        {
            _body.Content = body;
        }
        IsOpen = true;
        Visibility = Visibility.Visible;
    }

    public void Close()
    {
        IsOpen = false;
        Visibility = Visibility.Collapsed;
        if (_body is not null)
        {
            _body.Content = null;
        }
    }

    private void BuildUi()
    {
        // 遮罩（点击关闭）
        var backdrop = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0, 0, 0)),
        };
        backdrop.Tapped += (_, _) => Close();

        // 抽屉面板：右半区、Surface 背景、左侧边框
        _panel = new Border
        {
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(16),
        };

        var panelGrid = new Grid();
        panelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var closeBtn = new Button
        {
            Content = "✕",
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
        };
        closeBtn.Click += (_, _) => Close();
        Grid.SetColumn(_title, 0);
        Grid.SetColumn(closeBtn, 1);
        headerRow.Children.Add(_title);
        headerRow.Children.Add(closeBtn);

        _body = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        var bodyScroll = new ScrollViewer { Content = _body, Margin = new Thickness(0, 12, 0, 0) };

        Grid.SetRow(headerRow, 0);
        Grid.SetRow(bodyScroll, 1);
        panelGrid.Children.Add(headerRow);
        panelGrid.Children.Add(bodyScroll);
        _panel.Child = panelGrid;

        // 根：遮罩 + 面板（同一格，面板右对齐）
        var root = new Grid();
        root.Children.Add(backdrop);
        root.Children.Add(_panel);
        Content = root;
        Visibility = Visibility.Collapsed;
    }
}
