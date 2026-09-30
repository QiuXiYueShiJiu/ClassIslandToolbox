// 教学助手 v1.0.0：ClassIsland 置顶工具条插件
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIsland.Toolbox.Views;

/// <summary>饼图上的一块。</summary>
/// <param name="Label">这块是谁（姓名）。</param>
/// <param name="Value">这块的数值（被抽到的次数）。</param>
/// <param name="Color">这块的颜色。</param>
public sealed record PieSlice(string Label, double Value, Color Color);

/// <summary>
/// 自绘的饼状图。
/// </summary>
/// <remarks>
/// 没引第三方图表库：插件包里的每个依赖都得由宿主提供或者自己带，
/// 为一张饼图多背一个包不划算。这里用 Avalonia 的 <see cref="StreamGeometry"/>
/// 直接画，而且完全跟着主题走。
/// <para/>
/// <b>扇形是折线拟合出来的，不是 ArcTo。</b>两个原因：整圆（只有一块）时
/// <c>ArcTo</c> 的起点和终点重合、根本画不出东西，本来得为它单开一条椭圆分支；
/// 而极小角度下 <c>ArcTo</c> 的行为也不太好预期。改成按角度切片连折线之后，
/// 整圆和 0.1° 的细缝走的是同一条代码路径，没有特例。
/// 每 6° 一段，整圆 60 段，肉眼看不出棱角。
/// <para/>
/// 角度在设置 <see cref="Slices"/> 时就归一化好缓存起来，不在 <c>Render</c> 里算——
/// 重绘比换数据频繁得多。
/// </remarks>
public sealed class PieChartControl : Control
{
    /// <summary>折线拟合的角步长：6°。</summary>
    private const double SegmentAngle = Math.PI / 30;

    private readonly List<(double Start, double Sweep, Color Color)> _wedges = [];
    private IReadOnlyList<PieSlice> _slices = Array.Empty<PieSlice>();
    private double _total;

    /// <summary>要画的各块。赋值后立刻重算角度并重绘。</summary>
    public IReadOnlyList<PieSlice> Slices
    {
        get => _slices;
        set
        {
            _slices = value ?? Array.Empty<PieSlice>();
            RebuildWedges();
            InvalidateVisual();
        }
    }

    /// <summary>整块饼的外半径（逻辑像素）。四周会自动留出一点空隙。</summary>
    public double RadiusPadding { get; set; } = 10;

    /// <summary>块与块之间的细缝颜色。默认是半透明的灰，比纯黑描边干净。</summary>
    public Color SeparatorColor { get; set; } = Color.FromArgb(0x66, 0x88, 0x88, 0x88);

    /// <summary>把数据换算成「从哪个角度起、扫多少度」。0 或负数的块直接不算。</summary>
    private void RebuildWedges()
    {
        _wedges.Clear();
        _total = 0;

        foreach (var slice in _slices)
        {
            if (slice.Value > 0)
            {
                _total += slice.Value;
            }
        }

        if (_total <= 0)
        {
            return;
        }

        // 从 12 点方向起、顺时针铺。Avalonia 的 Y 轴朝下，所以 -90° 是正上方。
        var cursor = -Math.PI / 2;
        foreach (var slice in _slices)
        {
            if (slice.Value <= 0)
            {
                continue;
            }

            var sweep = slice.Value / _total * Math.PI * 2;
            _wedges.Add((cursor, sweep, slice.Color));
            cursor += sweep;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - RadiusPadding;
        if (radius <= 1)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var separator = new Pen(new SolidColorBrush(SeparatorColor), 1);

        if (_wedges.Count == 0)
        {
            // 一条记录都没有：画个空心圈占位，窗口里不至于空一块。
            context.DrawEllipse(null, separator, center, radius, radius);
            return;
        }

        foreach (var (start, sweep, color) in _wedges)
        {
            context.DrawGeometry(
                new SolidColorBrush(color),
                separator,
                BuildWedge(center, radius, start, sweep));
        }
    }

    /// <summary>把一段扇形摊成「圆心 + 一圈折线」的闭合图形。</summary>
    private static Geometry BuildWedge(Point center, double radius, double start, double sweep)
    {
        var steps = Math.Max(3, (int)Math.Ceiling(sweep / SegmentAngle));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(center, isFilled: true);

            // 多画一段走到 steps，保证末边正好落在 start + sweep 上。
            for (var i = 0; i <= steps; i++)
            {
                var angle = start + sweep * i / steps;
                ctx.LineTo(new Point(
                    center.X + radius * Math.Cos(angle),
                    center.Y + radius * Math.Sin(angle)));
            }

            ctx.EndFigure(true);
        }

        return geometry;
    }
}

/// <summary>
/// 饼图配色。
/// </summary>
/// <remarks>
/// 先用一组<b>手工挑的</b>基础色：相邻两块在色相和明暗上都拉开了距离，
/// 投影仪那种偏色环境下也能分清谁是谁。手挑的好处是前几个人——也就是
/// 实际最常出现的那些——拿到的是最耐看的颜色，而不是算出来什么就是什么。
/// <para/>
/// 名单长到超过基础色数量时，才在基础色上按轮次递进明暗继续循环，
/// 保证第 11 块不会和第 1 块撞脸。
/// </remarks>
public static class ChartPalette
{
    private static readonly Color[] BaseColors =
    [
        Color.FromRgb(0x4A, 0x90, 0xD9),   // 蓝
        Color.FromRgb(0xF2, 0x8B, 0x30),   // 橙
        Color.FromRgb(0x3F, 0x9E, 0x7C),   // 绿
        Color.FromRgb(0xD9, 0x53, 0x4F),   // 红
        Color.FromRgb(0x8E, 0x6F, 0xC4),   // 紫
        Color.FromRgb(0xC9, 0xA2, 0x27),   // 金
        Color.FromRgb(0x3C, 0xA8, 0xB8),   // 青
        Color.FromRgb(0xC7, 0x5B, 0x9B),   // 品红
        Color.FromRgb(0x6B, 0x8E, 0x3A),   // 橄榄
        Color.FromRgb(0x7A, 0x86, 0x94)    // 灰蓝
    ];

    /// <summary>取第 <paramref name="index"/> 块的颜色（超出基础色就按轮次调明暗）。</summary>
    public static Color ForIndex(int index)
    {
        if (index < 0)
        {
            index = 0;
        }

        var color = BaseColors[index % BaseColors.Length];
        var lap = index / BaseColors.Length;

        return lap == 0 ? color : Dim(color, lap);
    }

    /// <summary>
    /// 把颜色压暗若干档，用来区分「第几轮循环」。
    /// </summary>
    /// <remarks>
    /// 每轮压到上一轮的 82%，并且不低于 45% —— 再暗下去在投影上就成了一团黑。
    /// </remarks>
    private static Color Dim(Color color, int lap)
    {
        var factor = Math.Max(0.45, Math.Pow(0.82, lap));
        return Color.FromRgb(
            (byte)Math.Round(color.R * factor),
            (byte)Math.Round(color.G * factor),
            (byte)Math.Round(color.B * factor));
    }

    /// <summary>把 0~1 的比例格式化成百分比文字。</summary>
    public static string Percent(double share) =>
        (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
