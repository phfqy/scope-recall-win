using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ScopeRecallWin.Controls;

/// <summary>表格列定义（宽度 + 表头 + 取值 + 可选着色与悬停提示）。</summary>
public sealed class TableColumn
{
    public required string Header { get; init; }
    public double Width { get; init; } = 120;
    public required Func<object, string> Value { get; init; }

    /// <summary>可选：按行决定单元格颜色（语义色：成功绿 / 失败红 / 进行中蓝 / 警告琥珀）。</summary>
    public Func<object, Brush?>? Color { get; init; }

    /// <summary>可选：单元格悬停提示（常用于显示英文原名或完整值）。</summary>
    public Func<object, string?>? Tooltip { get; init; }
}

/// <summary>
/// 紧凑表格：表头 + 行 + 行点击 + 空态提示。
/// WinUI 无原生 DataGrid：Grid 表头 + ListView 行（行内单元格为代码构造的 Grid+TextBlock，
/// 原始行对象存于 Grid.Tag，行点击时经 RowTapped 回传）。
/// </summary>
public sealed partial class SimpleTable : UserControl
{
    /// <summary>行点击事件（参数为绑定的原始行对象）。</summary>
    public event EventHandler<object>? RowTapped;

    private ListView _list = null!;
    private Grid _header = null!;
    private TextBlock _empty = null!;

    public SimpleTable()
    {
        BuildUi();
    }

    public string EmptyText { get; set; } = "暂无数据";

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _header = new Grid
        {
            Padding = new Thickness(10, 6, 10, 6),
            Background = (Brush)Application.Current.Resources["Surface2Brush"],
        };
        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            Padding = new Thickness(0),
        };
        _list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is Grid { Tag: { } row })
            {
                RowTapped?.Invoke(this, row);
            }
        };

        _empty = new TextBlock
        {
            Margin = new Thickness(12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
        };

        Grid.SetRow(_header, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_empty, 1);
        root.Children.Add(_header);
        root.Children.Add(_list);
        root.Children.Add(_empty);
        Content = root;
    }

    /// <summary>绑定行数据并构建表头。</summary>
    public void BindRows<T>(IReadOnlyList<T> rows, IReadOnlyList<TableColumn> columns)
    {
        // 表头
        _header.ColumnDefinitions.Clear();
        _header.Children.Clear();
        foreach (var col in columns)
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(col.Width) });
            var tb = new TextBlock
            {
                Text = col.Header,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(tb, _header.ColumnDefinitions.Count - 1);
            _header.Children.Add(tb);
        }

        // 行
        var items = new List<Grid>();
        foreach (var row in rows)
        {
            var grid = new Grid { Padding = new Thickness(0, 5, 0, 5), Tag = row };
            for (int i = 0; i < columns.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columns[i].Width) });
                var tb = new TextBlock
                {
                    Text = columns[i].Value(row!) ?? "",
                    FontSize = 12.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                // 语义色：成功绿 / 失败红 / 进行中蓝 / 警告琥珀（失败额外加粗，便于扫视）
                if (columns[i].Color?.Invoke(row!) is { } brush)
                {
                    tb.Foreground = brush;
                    tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }

                // 悬停提示（英文原名或完整内容）
                if (columns[i].Tooltip?.Invoke(row!) is { Length: > 0 } tip)
                {
                    ToolTipService.SetToolTip(tb, tip);
                }

                Grid.SetColumn(tb, i);
                grid.Children.Add(tb);
            }
            items.Add(grid);
        }

        _empty.Text = EmptyText;
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.ItemsSource = items;
    }
}
