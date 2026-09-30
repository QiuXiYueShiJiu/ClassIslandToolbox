// 教学助手 v1.1.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System.Collections.Generic;

namespace ClassIsland.Toolbox.Models;

/// <summary>
/// 批注当前用哪支工具。
/// </summary>
/// <remarks>
/// 软笔和荧光笔是<b>两种笔</b>而不是"一种笔的两个颜色"：荧光笔半透明、粗得多，
/// 而且绘制时压在软笔<b>下面</b>——先划重点再在上面写字，字不能被高亮糊掉。
/// </remarks>
public enum AnnotateTool
{
    /// <summary>软笔：常规手写。</summary>
    Pen,

    /// <summary>荧光笔：半透明粗线，用来划重点。</summary>
    Highlighter,

    /// <summary>橡皮：按点擦除，一笔能被擦成两段。</summary>
    Eraser,

    /// <summary>激光笔：只留一条会自己消失的轨迹，不在屏幕上留下任何东西。</summary>
    Laser
}

/// <summary>
/// 批注能用的工具、颜色、粗细和橡皮尺寸。
/// </summary>
/// <remarks>
/// <b>刻意只给少而确定的选项。</b>批注是在课堂上随手用的，选项一多就成了"挑颜色"
/// 而不是"讲题"。这几个颜色和档位是投影仪和教室大屏上最容易看清的。
/// </remarks>
public static class AnnotationPalette
{
    /// <summary>软笔的颜色（顺序就是工具条上的顺序）。</summary>
    public static readonly IReadOnlyList<(string Name, string Hex, string Dot)> Pens =
    [
        ("红", "#FF3B30", "\U0001F534"),
        ("黄", "#FFCC00", "\U0001F7E1"),
        ("绿", "#34C759", "\U0001F7E2"),
        ("蓝", "#007AFF", "\U0001F535"),
        ("黑", "#1C1C1E", "\u26AB"),
        ("白", "#FFFFFF", "\u26AA")
    ];

    /// <summary>
    /// 荧光笔的颜色。
    /// </summary>
    /// <remarks>
    /// 只有浅色系：荧光笔的用途是"让底下的字透出来还能看清"，深色划上去等于涂黑。
    /// </remarks>
    public static readonly IReadOnlyList<(string Name, string Hex, string Dot)> Highlighters =
    [
        ("黄", "#FFEB3B", "\U0001F7E1"),
        ("绿", "#7BE86B", "\U0001F7E2"),
        ("青", "#4FD8EB", "\U0001F535"),
        ("粉", "#FF7AB8", "\U0001F7E3")
    ];

    /// <summary>荧光笔的不透明度（0~255）。太实就看不见底下的字了。</summary>
    public const byte HighlighterAlpha = 0x59;

    /// <summary>荧光笔比同档软笔粗多少倍。</summary>
    public const double HighlighterWidthFactor = 3.4;

    /// <summary>
    /// 软笔的三档粗细系数（乘以主按钮直径）。
    /// </summary>
    /// <remarks>
    /// 用系数而不是绝对像素：主按钮调大之后整条工具条会等比放大，
    /// 笔迹也该跟着粗一点，不然在大屏上会显得太细。
    /// </remarks>
    public static readonly IReadOnlyList<double> PenWidthFactors = [0.055, 0.09, 0.155];

    /// <summary>粗细档位对应的方块图标（小 / 中 / 大）。</summary>
    public static readonly IReadOnlyList<string> PenWidthDots = ["\u25AA", "\u25FC", "\u2B1B"];

    /// <summary>橡皮的半径（逻辑像素）：小 / 中 / 大。</summary>
    public static readonly IReadOnlyList<double> EraserRadii = [14, 30, 60];

    /// <summary>橡皮档位对应的方块图标。</summary>
    public static readonly IReadOnlyList<string> EraserDots = ["\u25FE", "\u25FC", "\u2B1B"];

    /// <summary>清屏。</summary>
    public const string ClearDot = "\U0001F300";

    /// <summary>撤销 / 重做。</summary>
    public const string UndoDot = "\u21B6";

    /// <inheritdoc cref="UndoDot"/>
    public const string RedoDot = "\u21B7";

    /// <summary>四支工具的图标。</summary>
    public const string PenToolDot = "\u270F\uFE0F";

    /// <inheritdoc cref="PenToolDot"/>
    public const string HighlighterToolDot = "\U0001F58D";

    /// <inheritdoc cref="PenToolDot"/>
    public const string EraserToolDot = "\U0001F9FC";

    /// <inheritdoc cref="PenToolDot"/>
    public const string LaserToolDot = "\U0001F534";

    /// <summary>退出批注。</summary>
    public const string ExitDot = "\u2716";

    /// <summary>激光笔轨迹存活多久（毫秒）。</summary>
    public const int LaserFadeMilliseconds = 900;

    /// <summary>激光笔的刷新间隔（毫秒）。约 30fps，再快肉眼也看不出。</summary>
    public const int LaserTickMilliseconds = 33;

    /// <summary>激光笔核心亮点的半径（逻辑像素）。</summary>
    public const double LaserCoreRadius = 7;

    /// <summary>激光笔外圈光晕的半径倍数。</summary>
    public const double LaserGlowFactor = 2.4;
}
