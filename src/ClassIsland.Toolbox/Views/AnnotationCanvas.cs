using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Toolbox.Models;

namespace ClassIsland.Toolbox.Views;

/// <summary>
/// 批注画布：软笔、荧光笔、橡皮、激光笔，画在全屏透明窗口上。
/// </summary>
/// <remarks>
/// <b>笔画存成点列，不存位图。</b>三个原因：
/// 一是橡皮要"擦掉一部分"，在位图上做就得处理混合模式（Avalonia 的 BlendMode 还在实验阶段），
/// 而按点删就是纯几何运算；二是清屏、撤销这类操作在点列上是常数时间；
/// 三是激光笔的轨迹可以"从末尾一直删到过期为止"，在位图上做不到。
/// <para/>
/// 代价是点会越积越多，所以落笔时做了抽稀——离上一个点太近就不记，肉眼看不出区别，
/// 点数能少一大半。
/// <para/>
/// <b>荧光笔和软笔分两层画。</b>荧光笔半透明而且宽，先划重点、再在上面写字是常见顺序；
/// 如果按落笔先后混在一起画，后写的字会被高亮盖住一层。所以渲染时永远先画全部荧光笔、
/// 再画全部软笔，不管它们实际是什么顺序画的。
/// </remarks>
public sealed class AnnotationCanvas : Control
{
    /// <summary>两条记录点之间的最小距离（逻辑像素）。比这近就不记，纯粹是省点数。</summary>
    private const double MinPointGap = 2.0;

    /// <summary>撤销栈最多存多少步。再多也用不上，白占内存。</summary>
    private const int MaxHistory = 60;

    private sealed class Stroke
    {
        public Color Color { get; init; }
        public double Thickness { get; init; }

        /// <summary>这条是荧光笔（半透明、画在软笔下面）。</summary>
        public bool IsHighlight { get; init; }

        public List<Point> Points { get; } = [];

        /// <summary>
        /// 这条笔画已经算好的路径。
        /// </summary>
        /// <remarks>
        /// <b>画完就缓存。</b>以前每一帧都要把所有笔画的所有点重新拼成路径，
        /// 屏幕上攒了几百个点之后，光是拼路径就够卡了——而这条路径其实再也不会变。
        /// 缓存之后每帧只剩一次绘制调用。
        /// </remarks>
        public Geometry? Geometry { get; set; }
    }

    /// <summary>激光笔轨迹上的一个采样点。</summary>
    private readonly record struct LaserPoint(Point Position, long At);

    private readonly List<Stroke> _strokes = [];
    private Stroke? _current;
    private bool _erasingNow;

    // ---- 撤销 / 重做 ----
    // 存的是**整个笔画列表的一份浅拷贝**：Stroke 对象本身画完就不再改动，
    // 所以拷贝一份引用就够了，不用把成千上万个点复制一遍。
    private readonly List<List<Stroke>> _undo = [];
    private readonly List<List<Stroke>> _redo = [];

    /// <summary>橡皮按下时的快照。真的擦掉东西了才提交进撤销栈。</summary>
    private List<Stroke>? _pendingErase;

    // ---- 激光笔 ----
    private readonly List<LaserPoint> _laser = [];
    private DispatcherTimer? _laserTimer;

    /// <summary>正在画的是哪根手指/笔。多点触控时别让第二根手指把笔画搅乱。</summary>
    private IPointer? _activePointer;

    /// <summary>画布是什么时候出来的。</summary>
    private long _bornAt;

    /// <summary>
    /// 刚出现多久之内的按下事件不认。
    /// </summary>
    /// <remarks>
    /// 上面那层"延后显示"是主要修复，这里是保险：万一还有别的地方在输入事件里把画布推出来，
    /// 也会补一个落点错误的按下事件。
    /// <para/>
    /// 250ms 足够隔开——误触的按下事件和窗口出现几乎同时，
    /// 而人从"点完按钮"到"抬笔开始写"最快也要三百多毫秒。
    /// </remarks>
    private const int ArmingMilliseconds = 250;

    /// <summary>软笔的颜色。</summary>
    public Color PenColor { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);

    /// <summary>软笔的粗细（逻辑像素）。</summary>
    public double PenThickness { get; set; } = 6;

    /// <summary>荧光笔的颜色（会自动按 <see cref="AnnotationPalette.HighlighterAlpha"/> 变半透明）。</summary>
    public Color HighlightColor { get; set; } = Color.FromRgb(0xFF, 0xEB, 0x3B);

    /// <summary>荧光笔的粗细（逻辑像素）。</summary>
    public double HighlightThickness { get; set; } = 20;

    /// <summary>现在用的是哪支工具。</summary>
    public AnnotateTool Tool { get; set; } = AnnotateTool.Pen;

    /// <summary>橡皮的半径（逻辑像素）。</summary>
    public double EraserRadius { get; set; } = 28;

    /// <summary>激光笔的颜色。</summary>
    public Color LaserColor { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);

    /// <summary>画布上有没有东西（激光轨迹不算，它自己会消失）。</summary>
    public bool IsEmpty => _strokes.Count == 0;

    /// <summary>现在有多少条笔迹。给测试和诊断用。</summary>
    public int StrokeCount => _strokes.Count;

    /// <summary>还能不能撤销。</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>还能不能重做。</summary>
    public bool CanRedo => _redo.Count > 0;

    public AnnotationCanvas()
    {
        ClipToBounds = true;
        _bornAt = Environment.TickCount64;
    }

    #region 撤销 / 重做

    /// <summary>
    /// 记一步历史。<b>必须在改动之前调</b>。
    /// </summary>
    private void PushHistory()
    {
        _undo.Add([.. _strokes]);
        if (_undo.Count > MaxHistory)
        {
            _undo.RemoveAt(0);
        }

        // 新动作发生之后，原来那条"未来"就回不去了。
        _redo.Clear();
    }

    /// <summary>撤销一步。</summary>
    /// <returns>真的撤销了才返回 true。</returns>
    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add([.. _strokes]);
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        ReplaceStrokes(previous);
        return true;
    }

    /// <summary>重做一步。</summary>
    /// <returns>真的重做了才返回 true。</returns>
    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Add([.. _strokes]);
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        ReplaceStrokes(next);
        return true;
    }

    private void ReplaceStrokes(List<Stroke> strokes)
    {
        _strokes.Clear();
        _strokes.AddRange(strokes);

        // 撤销可能把"已经算好路径"的那些笔画换回来，也可能换走。
        // 缓存是挂在 Stroke 对象上的，跟着对象走，所以这里只要把正在画的那条断掉就行。
        _current = null;
        _erasingNow = false;
        _pendingErase = null;
        InvalidateVisual();
    }

    #endregion

    /// <summary>清屏。可以撤销。</summary>
    public void Clear()
    {
        if (_strokes.Count == 0)
        {
            return;
        }

        PushHistory();
        _strokes.Clear();
        _current = null;
        InvalidateVisual();
    }

    /// <summary>把激光轨迹抹掉（换工具、退出批注时用）。</summary>
    public void ClearLaser()
    {
        if (_laser.Count == 0)
        {
            return;
        }

        _laser.Clear();
        _laserTimer?.Stop();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 先铺一层全透明的底。
        // 透明不等于"不画"：不画的话这块在窗口上就没有可命中的像素，笔和橡皮都落不下去。
        // （Panel 也是这个道理——它的命中测试看的就是 Background 是不是 null。）
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        // **两趟画**：荧光笔在下、软笔在上。见类注释。
        foreach (var stroke in _strokes)
        {
            if (stroke.IsHighlight)
            {
                DrawStroke(context, stroke);
            }
        }

        foreach (var stroke in _strokes)
        {
            if (!stroke.IsHighlight)
            {
                DrawStroke(context, stroke);
            }
        }

        RenderLaser(context);
    }

    private void DrawStroke(DrawingContext context, Stroke stroke)
    {
        if (stroke.Points.Count == 0)
        {
            return;
        }

        var brush = new SolidColorBrush(stroke.Color);

        if (stroke.Points.Count == 1)
        {
            // 点一下没移动：画个圆点，不然什么都看不到。
            var only = stroke.Points[0];
            context.DrawEllipse(brush, null, only, stroke.Thickness / 2, stroke.Thickness / 2);
            return;
        }

        var pen = new Pen(brush, stroke.Thickness,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        if (ReferenceEquals(stroke, _current))
        {
            // 正在画的这条每帧都得重算（点还在加），但它只有自己这些点。
            context.DrawGeometry(null, pen, BuildSmoothPath(stroke.Points));
            return;
        }

        // 已经画完的：路径不会变了，算一次就够。
        stroke.Geometry ??= BuildSmoothPath(stroke.Points);
        context.DrawGeometry(null, pen, stroke.Geometry);
    }

    /// <summary>
    /// 画激光笔：一条越往后越淡的拖尾 + 一个亮核。
    /// </summary>
    /// <remarks>
    /// 激光笔的语义是"指给人看"，所以它<b>不能在屏幕上留下任何东西</b>——
    /// 抬手之后轨迹自己消失，不用擦、不占撤销栈、也不参与"画布是不是空的"判断。
    /// </remarks>
    private void RenderLaser(DrawingContext context)
    {
        if (_laser.Count == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        var fade = (double)AnnotationPalette.LaserFadeMilliseconds;

        for (var i = 1; i < _laser.Count; i++)
        {
            var life = 1.0 - (now - _laser[i].At) / fade;
            if (life <= 0)
            {
                continue;
            }

            var width = AnnotationPalette.LaserCoreRadius * 1.15 * life;
            var pen = new Pen(
                new SolidColorBrush(LaserColor, life * 0.5),
                width,
                lineCap: PenLineCap.Round,
                lineJoin: PenLineJoin.Round);
            context.DrawLine(pen, _laser[i - 1].Position, _laser[i].Position);
        }

        var head = _laser[^1].Position;
        var radius = AnnotationPalette.LaserCoreRadius;

        // 外圈光晕 → 实心核 → 中间一点白，看起来才像"光"而不是一个红球。
        context.DrawEllipse(new SolidColorBrush(LaserColor, 0.20), null,
            head, radius * AnnotationPalette.LaserGlowFactor, radius * AnnotationPalette.LaserGlowFactor);
        context.DrawEllipse(new SolidColorBrush(LaserColor), null, head, radius, radius);
        context.DrawEllipse(Brushes.White, null, head, radius * 0.42, radius * 0.42);
    }

    /// <summary>
    /// 把一串点连成**平滑**的曲线。
    /// </summary>
    /// <remarks>
    /// 直接 LineTo 在快速划动时会看出明显的折线。这里用经典做法：
    /// 以相邻两点的中点为端点、原始点当控制点画二次贝塞尔——
    /// 曲线自然穿过中点、被原始点"拽"出弧度，笔迹就顺了。
    /// 计算量和连直线差不多，效果却是软笔该有的样子。
    /// </remarks>
    private static Geometry BuildSmoothPath(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false);

            if (points.Count == 2)
            {
                ctx.LineTo(points[1]);
            }
            else
            {
                for (var i = 1; i < points.Count - 1; i++)
                {
                    var control = points[i];
                    var end = new Point(
                        (points[i].X + points[i + 1].X) / 2,
                        (points[i].Y + points[i + 1].Y) / 2);
                    ctx.QuadraticBezierTo(control, end);
                }

                ctx.LineTo(points[^1]);
            }

            ctx.EndFigure(false);
        }

        return geometry;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var isDrawInput = e.Pointer.Type is PointerType.Touch or PointerType.Pen ||
                          e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (!isDrawInput)
        {
            return;
        }

        // 触摸屏上很容易搭上第二根手指（扶屏幕、手掌擦过）。
        // 只认第一根：正在画的时候，别的指针一律不理。
        if (_activePointer is not null)
        {
            return;
        }

        // 刚出来这一小会儿不收笔，见 ArmingMilliseconds 的说明。
        if (Environment.TickCount64 - _bornAt < ArmingMilliseconds)
        {
            return;
        }

        _activePointer = e.Pointer;
        var point = e.GetPosition(this);

        switch (Tool)
        {
            case AnnotateTool.Eraser:
                _erasingNow = true;

                // 先只记快照，等真的擦掉东西了再提交进撤销栈——
                // 否则空擦一下也会占掉一步撤销，用户按撤销会"没反应"。
                _pendingErase = [.. _strokes];
                Erase(point);
                break;

            case AnnotateTool.Laser:
                _laser.Clear();
                _laser.Add(new LaserPoint(point, Environment.TickCount64));
                StartLaserTimer();
                break;

            case AnnotateTool.Highlighter:
                PushHistory();
                _current = new Stroke
                {
                    Color = WithAlpha(HighlightColor, AnnotationPalette.HighlighterAlpha),
                    Thickness = HighlightThickness,
                    IsHighlight = true
                };
                _current.Points.Add(point);
                _strokes.Add(_current);
                break;

            default:
                PushHistory();
                _current = new Stroke { Color = PenColor, Thickness = PenThickness };
                _current.Points.Add(point);
                _strokes.Add(_current);
                break;
        }

        InvalidateVisual();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!ReferenceEquals(e.Pointer, _activePointer))
        {
            return;
        }

        if (_current is null && !_erasingNow && Tool != AnnotateTool.Laser)
        {
            return;
        }

        var point = e.GetPosition(this);

        switch (Tool)
        {
            case AnnotateTool.Eraser when _erasingNow:
                Erase(point);
                break;

            case AnnotateTool.Laser:
                _laser.Add(new LaserPoint(point, Environment.TickCount64));
                // 拖尾只留最近一小段，不然后面一直在遍历几万个历史点。
                TrimLaser();
                StartLaserTimer();
                InvalidateVisual();
                break;

            default:
                if (_current is not null && ShouldRecord(_current, point))
                {
                    _current.Points.Add(point);
                    InvalidateVisual();
                }

                break;
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!ReferenceEquals(e.Pointer, _activePointer))
        {
            return;
        }

        EndStroke(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// 指针捕获被系统收走了（切窗口、被别的程序抢走）。
    /// </summary>
    /// <remarks>
    /// 不处理的话 <c>_activePointer</c> 会一直挂着，之后**再也画不出东西**——
    /// 看起来就是"批注突然失灵了"。触摸屏上这种打断比鼠标多得多。
    /// </remarks>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (ReferenceEquals(e.Pointer, _activePointer))
        {
            EndStroke(null);
        }
    }

    private void EndStroke(IPointer? pointer)
    {
        _current = null;
        _erasingNow = false;
        _pendingErase = null;
        _activePointer = null;
        pointer?.Capture(null);
    }

    #region 激光笔计时器

    private void StartLaserTimer()
    {
        if (_laserTimer is not null)
        {
            return;
        }

        _laserTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AnnotationPalette.LaserTickMilliseconds)
        };
        _laserTimer.Tick += OnLaserTick;
        _laserTimer.Start();
    }

    private void OnLaserTick(object? sender, EventArgs e)
    {
        if (TrimLaser() == 0)
        {
            // 全过期了：停表，别让一个空计时器一直烧 CPU。
            _laserTimer?.Stop();
            _laserTimer = null;
        }

        InvalidateVisual();
    }

    /// <summary>扔掉过期的采样点。</summary>
    /// <returns>还剩几个点。</returns>
    private int TrimLaser()
    {
        var deadline = Environment.TickCount64 - AnnotationPalette.LaserFadeMilliseconds;
        var drop = 0;
        while (drop < _laser.Count && _laser[drop].At < deadline)
        {
            drop++;
        }

        if (drop > 0)
        {
            _laser.RemoveRange(0, drop);
        }

        return _laser.Count;
    }

    #endregion

    private static bool ShouldRecord(Stroke stroke, Point point)
    {
        var last = stroke.Points[^1];
        var dx = point.X - last.X;
        var dy = point.Y - last.Y;
        return dx * dx + dy * dy >= MinPointGap * MinPointGap;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// 擦掉半径内经过的点。
    /// </summary>
    /// <remarks>
    /// 一条笔画被擦断之后会变成两段——所以是「按点分段」而不是「整条删掉」：
    /// 橡皮擦过一条长线的中间，两头应该留着。
    /// <para/>
    /// 擦除<b>整段手势算一步撤销</b>：按下时先存快照，第一次真的擦掉东西时才提交。
    /// 不然擦一下会往撤销栈里塞几十步（每个移动事件一步）。
    /// </remarks>
    private void Erase(Point center)
    {
        var radiusSquared = EraserRadius * EraserRadius;
        var changed = false;

        for (var i = _strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _strokes[i];
            var pieces = new List<List<Point>>();
            List<Point>? piece = null;

            foreach (var point in stroke.Points)
            {
                var dx = point.X - center.X;
                var dy = point.Y - center.Y;

                if (dx * dx + dy * dy <= radiusSquared)
                {
                    piece = null;   // 断开，后面再遇到保留点就另起一段
                    continue;
                }

                if (piece is null)
                {
                    piece = [];
                    pieces.Add(piece);
                }

                piece.Add(point);
            }

            // 一个点都没被擦到：原样留着。
            if (pieces.Count == 1 && pieces[0].Count == stroke.Points.Count)
            {
                continue;
            }

            changed = true;
            _strokes.RemoveAt(i);
            _strokes.InsertRange(i, pieces.ConvertAll(p => new Stroke
            {
                Color = stroke.Color,
                Thickness = stroke.Thickness,
                IsHighlight = stroke.IsHighlight,
                Points = { }
            }));

            for (var k = 0; k < pieces.Count; k++)
            {
                _strokes[i + k].Points.AddRange(pieces[k]);
            }
        }

        if (!changed)
        {
            return;
        }

        // 快照是按下时拍的，里面存的是**原来的 Stroke 对象**——
        // 擦除只是把它们从列表里摘出去、换成新对象，原对象本身没被改过，
        // 所以这时候才提交也依然能完整还原。
        if (_pendingErase is not null)
        {
            _undo.Add(_pendingErase);
            if (_undo.Count > MaxHistory)
            {
                _undo.RemoveAt(0);
            }

            _redo.Clear();
            _pendingErase = null;
        }

        // 擦过之后点变了，缓存作废（新建的那些本来就是空的）。
        foreach (var stroke in _strokes)
        {
            if (!ReferenceEquals(stroke, _current))
            {
                stroke.Geometry = null;
            }
        }

        InvalidateVisual();
    }
}
