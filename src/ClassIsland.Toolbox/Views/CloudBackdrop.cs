// 教学助手 v1.0.0：ClassIsland 置顶工具条插件
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIsland.Toolbox.Views;

/// <summary>
/// 工具条的底：一片小小的天空，飘着几朵云。
/// </summary>
/// <remarks>
/// 自己画而不是贴图：一张云朵背景图要么做成九宫格拉伸（云会被拉长），
/// 要么随尺寸准备好几张（体积大、还得跟着换肤）。
/// 用几何图形画出来只有几十行，任何尺寸、任何圆角都好看，缩放也不会糊。
/// <para/>
/// 云朵由一组相互重叠的椭圆拼成——这也是画云最省事的办法：
/// 只要几个圆的高低错开，轮廓自然就是蓬松的。
/// <para/>
/// <b>云必须裁在圆角里面</b>，所以整块绘制套在
/// <see cref="DrawingContext.PushClip(RoundedRect)"/> 里；
/// 靠外层的 <c>ClipToBounds</c> 是裁不出圆角的。
/// </remarks>
public sealed class CloudBackdrop : Control
{
    /// <summary>圆角半径。给一个很大的值就等于「胶囊」（自动按短边的一半截断）。</summary>
    public double CornerRadius { get; set; } = 999;

    /// <summary>云的不透明度。工具条上按钮压着云，太实了会看不清字。</summary>
    public double CloudOpacity { get; set; } = 0.75;

    /// <summary>要不要画云。关掉就只剩天空渐变（菜单背景板就是这种）。</summary>
    public bool ShowClouds { get; set; } = true;

    /// <summary>描边颜色。</summary>
    public Color StrokeColor { get; set; } = Color.FromRgb(0x93, 0xC1, 0xEB);

    /// <summary>描边粗细。设成 0 就不描边。</summary>
    public double StrokeThickness { get; set; }

    // 天空要够蓝，白云才看得出来。太浅的话云等于没有。
    public Color TopColor { get; set; } = Color.FromRgb(0x8C, 0xC0, 0xEC);
    public Color MidColor { get; set; } = Color.FromRgb(0xC6, 0xE1, 0xFA);
    public Color BottomColor { get; set; } = Color.FromRgb(0xED, 0xF6, 0xFF);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var radius = Math.Max(0, Math.Min(CornerRadius, Math.Min(width, height) / 2));
        var shape = new RoundedRect(new Rect(0, 0, width, height), radius);

        var pen = StrokeThickness > 0
            ? new Pen(new SolidColorBrush(StrokeColor), StrokeThickness)
            : null;

        context.DrawRectangle(Sky(), pen, shape);

        using (context.PushClip(shape))
        {
            // 顶部撒一层很淡的白：云顶被阳光照着的那点亮。
            context.DrawRectangle(Sunlit(), null,
                new Rect(0, 0, width, Math.Max(1, height * 0.52)));

            if (ShowClouds)
            {
                DrawClouds(context, width, height);
            }
        }
    }

    /// <summary>自上而下：天蓝 → 淡蓝 → 近白。</summary>
    private IBrush Sky() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(TopColor, 0),
            new GradientStop(MidColor, 0.55),
            new GradientStop(BottomColor, 1)
        }
    };

    private static IBrush Sunlit() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
        }
    };

    /// <summary>
    /// 撒云。
    /// </summary>
    /// <remarks>
    /// 云的朵数按宽高比推：圆钮那种接近正方形的，一朵就够了；
    /// 展开后的长条按每 1.6 个高度一朵铺开，最多 5 朵。
    /// 高低左右都错开一点，免得看起来像一排等距的图案。
    /// </remarks>
    private void DrawClouds(DrawingContext context, double width, double height)
    {
        // ---- 小尺寸（消息胶囊、按钮）：一行飘几朵就够，再多是噪点 ----
        if (height < LargeThreshold)
        {
            var count = Math.Clamp((int)Math.Round(width / (height * 1.6)), 1, 5);

            for (var i = 0; i < count; i++)
            {
                // 云要飘在**天空还蓝着**的那一段。放到最底下等于压在近白底上，等于没有。
                DrawCloud(context, width * ((i + 0.5) / count),
                    height * (i % 2 == 0 ? 0.34 : 0.60),
                    height * (0.28 + 0.05 * (i % 3)),
                    CloudOpacity);
            }

            return;
        }

        // ---- 大卡片（幸运抽签弹窗）：铺开一片 ----
        // 卡片上只飘一两朵会显得很空。云要**小一些、密一些**才像天空，
        // 所以这里按网格铺：一行两三个、上下两三排，隔行横向错开半格，免得排成规整的格子。
        var columns = Math.Clamp((int)Math.Round(width / (height * 0.5)), 2, 5);
        var rows = Math.Clamp((int)Math.Round(height / (height * 0.5)), 1, 3);
        var radius = height * 0.16;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var index = row * columns + col;
                var offset = row % 2 == 0 ? 0 : 0.5;

                DrawCloud(context,
                    width * ((col + 0.5 + offset) / columns),
                    height * ((row + 0.62) / (rows + 0.42)),
                    radius * (0.82 + 0.36 * ((index % 3) / 2.0)),
                    CloudOpacity * 0.85);
            }
        }
    }

    /// <summary>超过这个高度就当"大卡片"，改用网格铺云。</summary>
    private const double LargeThreshold = 120;

    /// <summary>一朵云：四个互相重叠的椭圆。</summary>
    private static void DrawCloud(DrawingContext context, double cx, double cy, double r, double opacity)
    {
        var brush = new SolidColorBrush(Colors.White, opacity);

        // 左右两个小圆当"两肩"
        context.DrawEllipse(brush, null, new Point(cx - r * 0.64, cy + r * 0.12), r * 0.60, r * 0.44);
        context.DrawEllipse(brush, null, new Point(cx + r * 0.68, cy + r * 0.16), r * 0.54, r * 0.40);

        // 中间那个大的是"头"
        context.DrawEllipse(brush, null, new Point(cx, cy - r * 0.22), r * 0.78, r * 0.62);

        // 底下一条压平的长圆把整体连成一片
        context.DrawEllipse(brush, null, new Point(cx - r * 0.14, cy + r * 0.32), r * 0.94, r * 0.40);
    }
}
