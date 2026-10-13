using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ScopeRecallWin.Controls;

/// <summary>
/// 过滤/选择下拉（DropDownButton + MenuFlyout）。
/// 与 ComboBox 的区别：浮层固定从按钮下方展开，不会因为"当前选中项不是第一项"而整体上移。
/// 用法：SetItems(new[]{"全部状态","已生效",…}, selectedIndex: 0)；用 SelectedIndex / SelectedLabel 取值，
/// 或订阅 SelectionChanged。
/// </summary>
public sealed partial class FilterPicker : UserControl
{
    /// <summary>选中项变化（参数为新的索引）。</summary>
    public event EventHandler<int>? SelectionChanged;

    private readonly MenuFlyout _flyout = new();
    private readonly List<string> _labels = new();
    private readonly List<string?> _tags = new();
    private bool _suppress;

    public FilterPicker()
    {
        InitializeComponent();
        BtnPicker.Flyout = _flyout;
        BtnPicker.Click += (_, _) => RebuildMenu();
    }

    /// <summary>当前选中索引。</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>当前选中项文本。</summary>
    public string SelectedLabel => SelectedIndex >= 0 && SelectedIndex < _labels.Count ? _labels[SelectedIndex] : "";

    /// <summary>当前选中项附带的标签（可为 null）。</summary>
    public string? SelectedTag => SelectedIndex >= 0 && SelectedIndex < _tags.Count ? _tags[SelectedIndex] : null;

    /// <summary>组件上方的小标题（不设置则不显示）。</summary>
    public string Header
    {
        get => LblHeader.Text;
        set
        {
            LblHeader.Text = value;
            LblHeader.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>按钮最小宽度（默认 132）。</summary>
    public double ButtonMinWidth
    {
        get => BtnPicker.MinWidth;
        set => BtnPicker.MinWidth = value;
    }

    /// <summary>设置选项列表。</summary>
    public void SetItems(IEnumerable<string> labels, int selectedIndex = 0, IEnumerable<string?>? tags = null)
    {
        _labels.Clear();
        _labels.AddRange(labels);
        _tags.Clear();
        if (tags is not null)
        {
            _tags.AddRange(tags);
        }
        while (_tags.Count < _labels.Count)
        {
            _tags.Add(null);
        }

        _suppress = true;
        SelectedIndex = _labels.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _labels.Count - 1);
        TxtCurrent.Text = SelectedLabel;
        _suppress = false;
    }

    /// <summary>以代码方式选中某项。</summary>
    public void Select(int index, bool raiseEvent = false)
    {
        if (index < 0 || index >= _labels.Count || index == SelectedIndex)
        {
            return;
        }
        SelectedIndex = index;
        TxtCurrent.Text = SelectedLabel;
        if (raiseEvent)
        {
            SelectionChanged?.Invoke(this, index);
        }
    }

    /// <summary>按文本选中（找不到则保持原样）。</summary>
    public void SelectByLabel(string label, bool raiseEvent = false)
    {
        var index = _labels.IndexOf(label);
        if (index >= 0)
        {
            Select(index, raiseEvent);
        }
    }

    /// <summary>按附加标签选中（找不到则选中第一项）。</summary>
    public void SelectByTag(string? tag, bool raiseEvent = false)
    {
        var index = _tags.FindIndex(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        Select(index >= 0 ? index : 0, raiseEvent);
    }

    private void RebuildMenu()
    {
        _flyout.Items.Clear();
        for (int i = 0; i < _labels.Count; i++)
        {
            var index = i;
            var item = new RadioMenuFlyoutItem
            {
                Text = _labels[i],
                GroupName = Guid.NewGuid().ToString(),  // 每个 picker 独立分组，避免互相干扰
                IsChecked = i == SelectedIndex,
            };
            item.Click += (_, _) => OnPicked(index);
            _flyout.Items.Add(item);
        }
    }

    private void OnPicked(int index)
    {
        if (_suppress)
        {
            return;
        }
        SelectedIndex = index;
        TxtCurrent.Text = SelectedLabel;
        SelectionChanged?.Invoke(this, index);
    }
}
