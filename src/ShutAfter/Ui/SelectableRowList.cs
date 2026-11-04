using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShutAfter.Ui;

/// <summary>
/// 自绘的单选列表，每行支持"标题 + 副标题"两行。
///
/// 不用 ListBox 的原因：ListBox 的选中/悬停视觉来自系统主题模板，
/// 在深色界面里会冒出白底行、焦点虚线框这些与界面冲突的效果。
/// 这里每一行都是自绘 Border，只有被选中的那一行会上色，视觉和状态完全一致。
/// </summary>
public sealed class SelectableRowList
{
    private sealed class Row
    {
        public Border Box = null!;
        public TextBlock Title = null!;
        public TextBlock? Meta;
        public string Id = "";
    }

    private static readonly Brush TitleFg = Freeze(0xC6, 0xC9, 0xD4);
    private static readonly Brush MetaFg = Freeze(0x6B, 0x72, 0x86);
    private static readonly Brush HintFg = Freeze(0x51, 0x57, 0x69);
    private static readonly Brush SelectedBg = Freeze(0x17, 0x22, 0x38);
    private static readonly Brush SelectedBorder = Freeze(0x2A, 0x44, 0x70);
    private static readonly Brush SelectedTitle = Freeze(0xD9, 0xE5, 0xFF);
    private static readonly Brush SelectedMeta = Freeze(0x8C, 0xA6, 0xD6);
    private static readonly Brush HoverBg = Freeze(0x14, 0x16, 0x1F);

    private readonly Panel _host;
    private readonly Action<int> _onSelect;
    private readonly List<Row> _rows = new();
    private int _selected = -1;

    public SelectableRowList(Panel host, Action<int> onSelect)
    {
        _host = host;
        _onSelect = onSelect;
    }

    /// <summary>锁定后点击其他行不再改变选中项（监控运行中使用）。</summary>
    public bool Locked { get; set; }

    public int Count => _rows.Count;

    public int SelectedIndex => _selected;

    public string SelectedId => IdAt(_selected);

    public string IdAt(int index) =>
        index >= 0 && index < _rows.Count ? _rows[index].Id : "";

    public string TitleAt(int index) =>
        index >= 0 && index < _rows.Count ? _rows[index].Title.Text : "";

    public void Clear()
    {
        _host.Children.Clear();
        _rows.Clear();
        _selected = -1;
    }

    /// <summary>添加一行可选项。meta 为空时只显示单行标题。</summary>
    public void Add(string title, string id, string meta = "")
    {
        var label = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            Foreground = TitleFg,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        FrameworkElement content = label;
        TextBlock? metaBlock = null;

        if (meta.Length > 0)
        {
            metaBlock = new TextBlock
            {
                Text = meta,
                FontSize = 10.5,
                Foreground = MetaFg,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var stack = new StackPanel();
            stack.Children.Add(label);
            stack.Children.Add(metaBlock);
            content = stack;
        }

        var box = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 1, 0, 1),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = content
        };

        var index = _rows.Count;

        box.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            HandleClick(index);
        };

        box.MouseEnter += (_, _) =>
        {
            if (index != _selected) box.Background = HoverBg;
        };

        box.MouseLeave += (_, _) =>
        {
            if (index != _selected) box.Background = Brushes.Transparent;
        };

        _rows.Add(new Row { Box = box, Title = label, Meta = metaBlock, Id = id });
        _host.Children.Add(box);
    }

    /// <summary>添加一行不可选的提示文案（空列表、错误信息等）。</summary>
    public void AddHint(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = HintFg,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 10, 10, 10)
        };

        _host.Children.Add(label);

        // 占位但不进 _rows：它不参与选中、不影响索引
    }

    /// <summary>程序化设置选中项；notify 为 false 时不会回调，避免把内部同步当成用户点击。</summary>
    public void Select(int index, bool notify = false)
    {
        if (index < 0 || index >= _rows.Count)
            index = _rows.Count > 0 ? 0 : -1;

        var changed = index != _selected;
        _selected = index;

        for (var i = 0; i < _rows.Count; i++)
            Paint(i, i == _selected);

        if (_selected >= 0)
            _rows[_selected].Box.BringIntoView();

        if (notify && changed && _selected >= 0)
            _onSelect(_selected);
    }

    private void HandleClick(int index)
    {
        if (Locked || index == _selected) return;

        _selected = index;
        for (var i = 0; i < _rows.Count; i++)
            Paint(i, i == _selected);

        _onSelect(index);
    }

    private void Paint(int index, bool selected)
    {
        var row = _rows[index];
        row.Box.Background = selected ? SelectedBg : Brushes.Transparent;
        row.Box.BorderBrush = selected ? SelectedBorder : Brushes.Transparent;
        row.Title.Foreground = selected ? SelectedTitle : TitleFg;
        if (row.Meta != null)
            row.Meta.Foreground = selected ? SelectedMeta : MetaFg;
    }

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
