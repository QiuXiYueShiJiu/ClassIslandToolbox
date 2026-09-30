// 教学助手 v1.1.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Toolbox.Interop;

namespace ClassIsland.Toolbox.Views;

/// <summary>
/// 屏幕批注的全屏画布。
/// </summary>
/// <remarks>
/// 一整块铺满主屏的<b>透明</b>窗口，只有用户画上去的笔画是不透明的，
/// 所以看上去就是"直接在屏幕上写"。
/// <para/>
/// 几个关键设置：
/// <list type="bullet">
/// <item><c>Topmost</c>——要在课件、视频之上。</item>
/// <item><c>ShowActivated = false</c>——别把焦点从当前窗口抢走。老师往往是一边放课件一边批注。</item>
/// <item><c>TransparencyLevelHint = Transparent</c>——必须要，否则窗口会有一层实心底，整个屏幕被糊住。</item>
/// </list>
/// <para/>
/// 窗口本身<b>不做置顶复位</b>：工具条每秒会复位一次置顶，让工具条压在批注层上面，
/// 这样批注按钮始终点得到。两边都复位的话会互相打架、闪。
/// </remarks>
public sealed class AnnotationWindow : Window
{
    private static AnnotationWindow? _instance;

    private readonly AnnotationCanvas _canvas = new();

    /// <summary>现在收不收笔。见 <see cref="PauseOverlay"/>。</summary>
    private bool _interactive = true;

    /// <summary>当前打开着吗。</summary>
    public static bool IsOpen => _instance is not null;

    /// <summary>正在用的画布（没打开就是 null）。</summary>
    public static AnnotationCanvas? Canvas => _instance?._canvas;

    /// <summary>现在收不收笔（退出的画还留着，但不再收笔）。</summary>
    public static bool IsInteractive => _instance is { _interactive: true };

    /// <summary>打开（已经开着就返回现有的那个）。</summary>
    public static AnnotationWindow ShowOverlay()
    {
        if (_instance is not null)
        {
            return _instance;
        }

        var window = new AnnotationWindow { _interactive = true };

        // 先把实例挂上：画布要立刻可用，调用方紧接着就要往里推笔和橡皮的设置。
        _instance = window;

        // **窗口不能在这一刻显示。** 这里是在工具条按钮的 PointerReleased 处理过程当中，
        // 输入事件还没走完。此时冒出一个全屏窗口，系统会把"指针还按着"的状态补一个
        // 按下事件给新窗口——落点正是刚才那颗按钮的位置，于是第一笔会多出一条
        // 从按钮连到落笔处的线。丢到消息队列后面，等这一轮输入彻底结束再显示。
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_instance, window))
            {
                return;
            }

            window.Show();
            window.FitToScreen();
        }, DispatcherPriority.Background);

        return window;
    }

    /// <summary>
    /// 退出批注：**画留着，把屏幕还给系统**。
    /// </summary>
    /// <remarks>
    /// 不能直接 <c>Close()</c>——画布是窗口的一部分，关掉窗口等于把画一起丢了，
    /// 再进来就是一张白纸。这里只是把窗口变成"点击穿透"：
    /// 画还显示在屏幕上，鼠标和触屏已经能点到下面的程序了。
    /// 想真的清掉画，用橡皮那一行的「清屏」。
    /// </remarks>
    public static void PauseOverlay()
    {
        if (_instance is null)
        {
            return;
        }

        _instance._interactive = false;
        TopmostEnforcer.SetClickThrough(_instance, true);
    }

    /// <summary>回到批注：重新开始收笔。</summary>
    public static void ResumeOverlay()
    {
        if (_instance is null)
        {
            return;
        }

        _instance._interactive = true;
        TopmostEnforcer.SetClickThrough(_instance, false);
    }

    /// <summary>真正销毁（插件停止、工具条隐藏时用）。</summary>
    public static void CloseOverlay()
    {
        var window = _instance;
        _instance = null;
        window?.Close();
    }

    private AnnotationWindow()
    {
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        // 完全透明：批注是"直接在屏幕上写"，盖一层暗色会把课件压灰。
        // "现在是不是在批注状态"由工具条上那颗按钮变暗来表达，不用动整个屏幕。
        Background = new SolidColorBrush(Colors.Transparent);
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = _canvas;
    }

    /// <summary>
    /// 铺满主屏。
    /// </summary>
    /// <remarks>
    /// <c>Screen.Bounds</c> 是<b>物理</b>像素，而 <c>Width/Height</c> 和 <c>Position</c> 的口径不一样，
    /// 所以要按缩放比换算——高分屏上不换算会只铺到屏幕的一部分。
    /// </remarks>
    private void FitToScreen()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;

        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / scaling;
        Height = screen.Bounds.Height / scaling;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }
}
