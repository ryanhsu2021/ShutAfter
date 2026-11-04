using System;
using System.Windows;
using System.Windows.Media;

namespace ShutAfter.Ui;

/// <summary>
/// 极简进度环：一圈底轨 + 一段圆弧。
///
/// 比横向进度条更合适的原因：状态卡里它同时承担"进度"和"状态灯"两个角色，
/// 颜色随状态切换（蓝=监控中 / 橙=倒计时 / 绿=完成），一眼就能看出当前处在哪一段。
/// 直接 OnRender 绘制，不引入任何第三方控件库，每秒重绘一次的开销可以忽略。
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0~1。小于 0.0005 只画底轨，大于 0.9995 画整圈。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 6 || h <= 6) return;

        var thickness = Math.Max(1.0, StrokeThickness);
        var radius = (Math.Min(w, h) - thickness) / 2;
        var center = new Point(w / 2, h / 2);

        var trackPen = new Pen(TrackBrush ?? Brushes.DimGray, thickness);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var v = Math.Clamp(Value, 0, 1);
        if (v <= 0.0005) return;

        var accentPen = new Pen(AccentBrush ?? Brushes.DeepSkyBlue, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        if (v >= 0.9995)
        {
            dc.DrawEllipse(null, accentPen, center, radius, radius);
            return;
        }

        var start = -Math.PI / 2;                  // 12 点方向起步
        var end = start + v * Math.PI * 2;
        var from = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        var to = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.ArcTo(to, new Size(radius, radius), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();

        dc.DrawGeometry(null, accentPen, geometry);
    }
}
