using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIsland.Toolbox.Views;

/// <summary>
/// 一层云：在控件里占哪个矩形，以及填充的不透明度。
/// </summary>
/// <param name="X">左边缘（0~1，相对控件宽度）。</param>
/// <param name="Y">上边缘（0~1，相对控件高度）。</param>
/// <param name="W">宽度（0~1）。</param>
/// <param name="H">高度（0~1）。</param>
/// <param name="Opacity">这一层的填充不透明度。后面那朵调淡一点，才有前后层次。</param>
public sealed record CloudLayer(double X, double Y, double W, double H, double Opacity = 1.0);

/// <summary>
/// 一朵云形状的按钮底。
/// </summary>
/// <remarks>
/// 和「白色胶囊」的区别在于：轮廓本身就是云的形状——几个鼓包和一道平底，
/// 描边也跟着这个形状走。做法是算出几个椭圆的<b>并集外轮廓</b>，再当成一条闭合路径画。
/// <para/>
/// <b>为什么不直接用 <c>Geometry.Combine</c>：</b>Avalonia 的签名是
/// <c>Combine(Geometry, RectangleGeometry, GeometryCombineMode, Transform)</c>——
/// 第二个参数写死成矩形，两个椭圆并不起来。
/// <para/>
/// 所以这里用<b>射线法</b>求轮廓：以云的质心为原点，向四周打一圈射线，
/// 每条射线取「和所有椭圆交点的最远值」，连起来就是并集的外边界。
/// 云是近似星形的（从质心看出去不会被自己挡住），这个方法足够准，
/// 而且结果是<b>一条闭合路径</b>，能填也能描边——这是关键，用一堆椭圆直接画的话
/// 填充看不出来，描边会把每个圆的内部弧线也画出来。
/// <para/>
/// 轮廓按尺寸缓存：只在换尺寸档位或改圆钮大小时才需要重算。
/// </remarks>
public sealed class CloudButtonSurface : Control
{
    private Geometry? _outline;
    private Geometry? _shadow;
    private List<Geometry>? _layerOutlines;
    private bool _geometryReady;
    private double _cachedWidth;
    private double _cachedHeight;

    /// <summary>
    /// 多层云。给了就按这个一层层画（主按钮就是两片部分重叠的云）；
    /// 不给就按整块尺寸画一整朵。
    /// </summary>
    /// <remarks>
    /// <b>每一层都是独立的一朵云、各自有轮廓</b>，叠在一起但是看得出是几片——
    /// 这正是"两片部分重叠的云朵"要的效果。之前用几个椭圆求并集拼成一整块，
    /// 看着是一个鼓包的整体，像套了个圆形背景。
    /// </remarks>
    public IReadOnlyList<CloudLayer>? Layers { get; set; }

    /// <summary>云的填充色。</summary>
    public Color Fill { get; set; } = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);

    /// <summary>云的描边色。</summary>
    public Color Stroke { get; set; } = Color.FromRgb(0x93, 0xC1, 0xEB);

    /// <summary>描边粗细。</summary>
    public double StrokeThickness { get; set; } = 1.4;

    /// <summary>云底下的软影子。用同一个形状往下挪一点画一遍，比高斯模糊便宜得多。</summary>
    public Color ShadowColor { get; set; } = Color.FromArgb(0x45, 0x5F, 0x9E, 0xD8);

    /// <summary>换填充色（悬停、确认态用）。</summary>
    public void SetFill(Color color)
    {
        Fill = color;
        InvalidateVisual();
    }

    /// <summary>换描边色。</summary>
    public void SetStroke(Color color)
    {
        Stroke = color;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 3 || height <= 3)
        {
            return;
        }

        EnsureGeometry(width, height);

        var pen = new Pen(new SolidColorBrush(Stroke), StrokeThickness);

        if (_layerOutlines is { Count: > 0 })
        {
            // 一层层画，后面的先画。每层描边都描自己的轮廓，于是能看出一片压着一片。
            for (var i = 0; i < _layerOutlines.Count; i++)
            {
                var opacity = Layers![i].Opacity;
                var fill = new SolidColorBrush(Fill, Fill.A / 255.0 * opacity);
                context.DrawGeometry(fill, pen, _layerOutlines[i]);
            }

            return;
        }

        if (_shadow is not null)
        {
            context.DrawGeometry(new SolidColorBrush(ShadowColor), null, _shadow);
        }

        if (_outline is not null)
        {
            context.DrawGeometry(new SolidColorBrush(Fill), pen, _outline);
        }
    }

    private void EnsureGeometry(double width, double height)
    {
        // 用单独的标志判断，别拿 _outline 是否为 null 当"算过没有"——
        // 分层模式（主按钮）下 _outline 永远是 null，那样每帧都会重算一遍轮廓。
        if (_geometryReady &&
            Math.Abs(_cachedWidth - width) < 0.5 &&
            Math.Abs(_cachedHeight - height) < 0.5)
        {
            return;
        }

        _cachedWidth = width;
        _cachedHeight = height;
        _geometryReady = true;

        if (Layers is { Count: > 0 })
        {
            _outline = null;
            _shadow = null;
            _layerOutlines = [];

            foreach (var layer in Layers)
            {
                _layerOutlines.Add(BuildSingleCloud(
                    layer.X * width, layer.Y * height, layer.W * width, layer.H * height));
            }

            return;
        }

        _layerOutlines = null;

        var drop = height * 0.05;
        _outline = BuildPath(TraceOutline(Puffs(width, height), Centroid(width, height)), 0);
        _shadow = BuildPath(TraceOutline(Puffs(width, height), Centroid(width, height)), drop);
    }

    /// <summary>在指定矩形里画一整朵云（用按钮那套四个鼓包）。</summary>
    private static Geometry BuildSingleCloud(double x, double y, double w, double h)
    {
        var puffs = new (double Cx, double Cy, double Rx, double Ry)[]
        {
            (x + w * 0.21, y + h * 0.60, w * 0.21, h * 0.25),
            (x + w * 0.79, y + h * 0.60, w * 0.19, h * 0.23),
            (x + w * 0.50, y + h * 0.39, w * 0.31, h * 0.31),
            (x + w * 0.50, y + h * 0.75, w * 0.46, h * 0.24)
        };

        return BuildPath(TraceOutline(puffs, new Point(x + w * 0.5, y + h * 0.55)), 0);
    }

    /// <summary>
    /// 云由四个椭圆拼成：左右两个"肩"、中间那个大的"头"、底下一道压平的长圆。
    /// </summary>
    /// <summary>
    /// 拼出一朵云的那几个椭圆：左右两个"肩"、中间一个大的"头"、底下一道压平的长圆。
    /// </summary>
    private static (double Cx, double Cy, double Rx, double Ry)[] Puffs(double w, double h) =>
    [
        (w * 0.21, h * 0.60, w * 0.21, h * 0.25),   // 左肩
        (w * 0.79, h * 0.60, w * 0.19, h * 0.23),   // 右肩
        (w * 0.50, h * 0.39, w * 0.31, h * 0.31),   // 中间那个大的"头"
        (w * 0.50, h * 0.75, w * 0.46, h * 0.24)    // 底下一条压平的长圆
    ];

    /// <summary>射线要打出去的原点。必须落在云里面。</summary>
    private static Point Centroid(double w, double h) => new(w * 0.50, h * 0.55);

    /// <summary>
    /// 射线法求并集外轮廓。
    /// </summary>
    /// <remarks>
    /// 射线取 <c>P(t) = 质心 + t·(cosθ, sinθ)</c>，往每个椭圆里代：
    /// <c>((P.x-cx)/rx)² + ((P.y-cy)/ry)² = 1</c> 是个关于 t 的一元二次方程，
    /// 取正根就是"从质心出发穿出这个椭圆的距离"。对四个椭圆取最大值，
    /// 就是这条射线上并集的边界。
    /// </remarks>
    private static List<Point> TraceOutline(
        (double Cx, double Cy, double Rx, double Ry)[] puffs, Point origin)
    {
        const int steps = 168;   // 每 2.1° 一个采样点，小尺寸下已经看不出棱角

        var ox = origin.X;
        var oy = origin.Y;

        var points = new List<Point>(steps);

        for (var i = 0; i < steps; i++)
        {
            var angle = i * 2 * Math.PI / steps;
            var dx = Math.Cos(angle);
            var dy = Math.Sin(angle);

            var farthest = 0.0;

            foreach (var puff in puffs)
            {
                var ex = ox - puff.Cx;
                var ey = oy - puff.Cy;

                var a = dx * dx / (puff.Rx * puff.Rx) + dy * dy / (puff.Ry * puff.Ry);
                var b = 2 * (ex * dx / (puff.Rx * puff.Rx) + ey * dy / (puff.Ry * puff.Ry));
                var c = ex * ex / (puff.Rx * puff.Rx) + ey * ey / (puff.Ry * puff.Ry) - 1;

                var discriminant = b * b - 4 * a * c;
                if (discriminant <= 0 || a <= 0)
                {
                    continue;
                }

                var t = (-b + Math.Sqrt(discriminant)) / (2 * a);
                if (t > farthest)
                {
                    farthest = t;
                }
            }

            if (farthest <= 0)
            {
                // 理论上不会发生（质心在云里面）；真发生了就贴着鼓包走一小步，别把轮廓画崩。
                farthest = puffs[0].Rx * 0.25;
            }

            points.Add(new Point(ox + dx * farthest, oy + dy * farthest));
        }

        return points;
    }

    private static Geometry BuildPath(IReadOnlyList<Point> points, double offsetY)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y + offsetY), isFilled: true);
            for (var i = 1; i < points.Count; i++)
            {
                ctx.LineTo(new Point(points[i].X, points[i].Y + offsetY));
            }

            ctx.EndFigure(isClosed: true);
        }

        return geometry;
    }
}
