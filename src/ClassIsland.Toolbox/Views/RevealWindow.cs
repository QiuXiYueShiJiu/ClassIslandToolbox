using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using ClassIsland.Toolbox.Interop;

namespace ClassIsland.Toolbox.Views;

/// <summary>
/// 抽到人之后亮在屏幕中央的那朵云。
/// </summary>
/// <remarks>
/// <b>整张卡片就是一朵云，没有别的背景。</b>窗口本身是透明的，云外那圈什么都没有，
/// 所以看上去就是「一朵云飘在课件上」，而不是「一个窗口里画了朵云」。
/// 云里只写名字，不写「幸运抽签结果」这类标签——抽到什么一眼就看得到，
/// 再挂一行说明只是抢注意力。
/// <para/>
/// 两个关键设计：
/// <list type="bullet">
/// <item><b>窗口只有云那么大，不铺满屏幕。</b>早先版本是整屏窗口，结果它把悬浮钮盖住了，
///       连着抽人时第二下点的是它而不是钮，表现就是「点不动」。
///       试过用 <c>WS_EX_TRANSPARENT</c> 让它穿透，但那个样式只在窗口同时是 LAYERED 时
///       才对命中测试生效，而给 Avalonia 的透明窗口补 <c>WS_EX_LAYERED</c> 会打乱它自己的合成，
///       窗口直接不显示。两条路都堵死，索性从根上解决：窗口不覆盖屏幕，就没有挡不挡的问题。</item>
/// <item><b>单例复用。</b>再抽一次不新开窗口，直接换掉里面的名字并重置计时，
///       避免连点时叠出一摞窗口。</item>
/// </list>
/// </remarks>
public class RevealWindow : Window
{
    // 和工具条同一套「云 + 天空」：淡蓝描边、深蓝灰的字。
    private static readonly Color CloudBorder = Color.FromRgb(0x93, 0xC1, 0xEB);
    private static readonly Color CloudFill = Color.FromArgb(0xFF, 0xFA, 0xFD, 0xFF);
    private static readonly Color NameColor = Color.FromRgb(0x1F, 0x6F, 0xD4);
    private static readonly Color LeadColor = Color.FromRgb(0x8A, 0x9E, 0xB1);

    /// <summary>
    /// 名字用的字体。
    /// </summary>
    /// <remarks>
    /// 按平台常见的中文字体排一串，交给系统挑第一个装了的：
    /// Windows 上是微软雅黑，macOS 上苹方，Linux 上思源黑体。
    /// <b>不写死单一字体</b>——插件要跨平台，写死一个系统没有的字体就会掉回默认字形，
    /// 大屏幕上那个默认字形很难看。
    /// </remarks>
    private static readonly FontFamily UiFont = new(
        "Microsoft YaHei UI, Microsoft YaHei, PingFang SC, Hiragino Sans GB, " +
        "Source Han Sans SC, Noto Sans CJK SC, sans-serif");

    /// <summary>云面比名字大多少（倍数）。太小会显得字挤满整朵云。</summary>
    private const double CloudPaddingX = 1.25;

    private const double CloudPaddingTop = 0.95;

    private const double CloudPaddingBottom = 0.80;

    /// <summary>
    /// 入场过渡：淡入 + 轻微放大。
    /// </summary>
    /// <remarks>
    /// 两条过渡共用同一套缓动和时长口径，视觉上才像"一次动作"而不是先后两段。
    /// 抽成工厂而不是就地写死，是为了调时长时只改一处。
    /// </remarks>
    private static Transitions EntranceTransitions() =>
    [
        FadeIn(OpacityProperty, 140),
        GrowIn(RenderTransformProperty, 220)
    ];

    private static DoubleTransition FadeIn(AvaloniaProperty property, int milliseconds) => new()
    {
        Property = property,
        Duration = TimeSpan.FromMilliseconds(milliseconds),
        Easing = new CubicEaseOut()
    };

    private static TransformOperationsTransition GrowIn(AvaloniaProperty property, int milliseconds) => new()
    {
        Property = property,
        Duration = TimeSpan.FromMilliseconds(milliseconds),
        Easing = new CubicEaseOut()
    };

    private static RevealWindow? _instance;

    /// <summary>
    /// 窗口本身的外观。跟内容无关，单独放一处。
    /// </summary>
    /// <remarks>
    /// 三组设置分别对应三件事：<b>无形</b>（无边框、全透明）、
    /// <b>不打扰</b>（置顶但不抢焦点、不进任务栏、不可缩放）、
    /// <b>不拦截</b>（点击穿透）。放在一起看，比散在构造函数里清楚。
    /// </remarks>
    private void ConfigureWindow()
    {
        // 无形：云外那圈要真的是什么都没有，不能有一层窗口底。
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = null;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 不打扰：盖在课件上，但绝不把老师的操作打断。
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;

        // 不拦截：这东西只是给人看的，不该吃掉任何点击。
        IsHitTestVisible = false;
    }

    private readonly TextBlock _lead;
    private readonly TextBlock _nameText;
    private readonly StackPanel _lines;
    private readonly CloudButtonSurface _cloud;
    private readonly Panel _stage;
    private readonly DispatcherTimer _closeTimer;
    private TopmostEnforcer? _topmost;
    private bool _closing;

    /// <summary>
    /// 亮出一个名字。已经有窗口开着就复用它，否则新建。
    /// </summary>
    /// <param name="name">要显示的名字。</param>
    /// <param name="fontSize">字号（逻辑像素）。云的大小是按它推出来的。</param>
    /// <param name="hold">停留多久之后自己淡出。</param>
    public static void Show(string name, double fontSize, TimeSpan hold)
    {
        var window = Ensure(hold, fontSize);
        window.ShowName(name, fontSize, hold);
    }

    /// <summary>把当前开着的弹窗立刻收掉（比如插件停止时）。</summary>
    public static void CloseCurrent() => _instance?.FadeOutAndClose();

    /// <summary>拿到可用的窗口实例：已经开着就复用，否则新建。</summary>
    private static RevealWindow Ensure(TimeSpan hold, double fontSize)
    {
        if (_instance is { _closing: false } existing)
        {
            return existing;
        }

        var window = new RevealWindow(fontSize, hold);
        _instance = window;
        window.Show();
        return window;
    }

    private RevealWindow(double fontSize, TimeSpan hold)
    {
        ConfigureWindow();

        // 第一行「有请——」：小字、灰色、靠左。
        // 靠左是刻意的——它是一句引子，跟下面居中的名字拉开，看着才像"念到名字"。
        _lead = new TextBlock
        {
            Text = "有请——",
            FontSize = fontSize * 0.24,
            FontFamily = UiFont,
            Foreground = new SolidColorBrush(LeadColor),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(fontSize * 0.10, 0, 0, fontSize * 0.08)
        };

        // 第二行：名字。大字、蓝色、加粗。
        _nameText = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = FontWeight.Bold,
            FontFamily = UiFont,
            Foreground = new SolidColorBrush(NameColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(280, fontSize * 4.6)
        };

        _lines = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _lead, _nameText }
        };

        // 整朵云就是一个控件，把名字压在上面。
        // **留白加在名字上，不加在云上**：云是按自己的边界画的，
        // 给它加 Padding 会把云面挤变形，而不是让名字往里缩。
        _cloud = new CloudButtonSurface
        {
            Fill = CloudFill,
            Stroke = CloudBorder,
            StrokeThickness = Math.Max(2.0, fontSize * 0.022),
            ShadowColor = Color.FromArgb(0x3A, 0x5F, 0x9E, 0xD8)
        };

        _stage = new Panel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _cloud, _lines },
            Opacity = 0,
            // 用 TransformOperations 而不是自己搭 ScaleTransform：
            // 过渡挂在 Visual 上才有自己的时钟；单独的 ScaleTransform 不是 Visual，
            // 交给 Animation.RunAsync 会抛类型转换异常。
            RenderTransform = TransformOperations.Parse("scale(0.92)"),
            RenderTransformOrigin = RelativePoint.Center,
            Transitions = EntranceTransitions()
        };

        ApplyScale(fontSize);
        Content = new Panel { Children = { _stage } };

        _closeTimer = new DispatcherTimer { Interval = hold };
        _closeTimer.Tick += (_, _) => FadeOutAndClose();
    }

    /// <summary>按字号把云面撑到比名字大一圈。</summary>
    private void ApplyScale(double fontSize) =>
        _lines.Margin = new Thickness(
            fontSize * CloudPaddingX,
            fontSize * CloudPaddingTop,
            fontSize * CloudPaddingX,
            fontSize * CloudPaddingBottom);

    /// <summary>复用当前窗口显示一个名字。</summary>
    private void ShowName(string name, double fontSize, TimeSpan hold)
    {
        _nameText.Text = name;
        _nameText.FontSize = fontSize;
        _lead.FontSize = fontSize * 0.24;
        _lead.Margin = new Thickness(fontSize * 0.10, 0, 0, fontSize * 0.08);
        ApplyScale(fontSize);
        Bump(hold);
    }

    /// <summary>缩一下再弹回来，给出「换了一个」的反馈，比原地换内容更容易察觉。</summary>
    private void Bump(TimeSpan hold)
    {
        _closeTimer.Stop();
        _stage.RenderTransform = TransformOperations.Parse("scale(0.94)");
        Dispatcher.UIThread.Post(
            () => _stage.RenderTransform = TransformOperations.Parse("scale(1)"),
            DispatcherPriority.Render);

        _stage.Opacity = 1;
        _closeTimer.Interval = hold;
        _closeTimer.Start();
        _topmost?.Reassert();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        CenterOnScreen();
        SizeChanged += (_, _) => CenterOnScreen();

        _topmost = new TopmostEnforcer(this, TimeSpan.FromMilliseconds(400));
        _topmost.Attach();

        // 入场：设一次目标值，剩下的交给上面挂好的过渡。
        _stage.Opacity = 1;
        _stage.RenderTransform = TransformOperations.Parse("scale(1)");

        _closeTimer.Start();
    }

    /// <summary>
    /// 把窗口摆到当前屏幕正中。
    /// </summary>
    /// <remarks>
    /// 名字长短不同，云的宽度也不同，所以尺寸一变就得重摆一次（<c>SizeChanged</c> 里再调）。
    /// </remarks>
    private void CenterOnScreen()
    {
        if (ResolveTargetOrigin() is not { } origin)
        {
            return;
        }

        Position = origin;
    }

    /// <summary>
    /// 算出窗口左上角该落在哪个物理像素位置。拿不到屏幕信息就返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <c>Screen.Bounds</c> 是<b>物理</b>像素，窗口的 <c>Bounds</c> 是<b>逻辑</b>像素，
    /// 两个口径不一样，必须按缩放比换算——高分屏上不换算会只盖住屏幕的一部分。
    /// 屏幕原点也未必是 (0,0)：多显示器时副屏带着自己的偏移，所以要拿它的 X/Y 打底。
    /// </remarks>
    private PixelPoint? ResolveTargetOrigin()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return null;
        }

        var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var area = screen.Bounds;
        return new PixelPoint(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2);
    }

    private void FadeOutAndClose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _closeTimer.Stop();
        PlayExitAnimation();

        // 动画是异步的，得等它跑完再真的关窗口——立刻 Close 会把退场吃掉，
        // 表现就是"啪"地消失而不是淡出。
        var fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        fade.Tick += (_, _) => FinishClose(fade);
        fade.Start();
    }

    /// <summary>退场动画：缩小一点、透明度归零。</summary>
    private void PlayExitAnimation()
    {
        _stage.Opacity = 0;
        _stage.RenderTransform = TransformOperations.Parse("scale(0.96)");
    }

    private void FinishClose(DispatcherTimer fade)
    {
        fade.Stop();
        _topmost?.Dispose();

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        Close();
    }
}
