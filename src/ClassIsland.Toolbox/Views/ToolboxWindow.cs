using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using ClassIsland.Toolbox.Interop;
using ClassIsland.Toolbox.Models;
using ClassIsland.Toolbox.Services;

namespace ClassIsland.Toolbox.Views;

/// <summary>
/// 置顶工具条：一个写着「工具」的圆钮，点一下向两边展开成一排快捷按钮。
/// </summary>
/// <remarks>
/// <b>展开后圆钮居中，功能在两侧均分</b>（个数为奇数时右边多一个）。
/// 这样展开的瞬间圆钮不会跳——它一直是视觉重心，用户的鼠标也正好停在上面。
/// <para/>
/// <b>「不鬼畜」是硬要求，靠三件事保证：</b>
/// <list type="number">
/// <item>按钮宽度在建的时候就量死，标题变化（比如确认态的「再点一次」）不会引起重排；</item>
/// <item>展开动画只做透明度 + <c>translate</c>，<b>不碰布局</b>，
///       所以窗口尺寸和位置在第一帧就定死，圆钮不会被动画带着抖；</item>
/// <item>先摆好窗口、下一帧再淡入内容，避免「圆钮先跳一下、按钮再冒出来」。</item>
/// </list>
/// <para/>
/// 布局是 <c>[ 左侧按钮组 ][ 圆钮 ][ 右侧按钮组 ]</c> 三段固定顺序，
/// 展开时只是把两侧的按钮组显示出来，不做任何重排。
/// 窗口用 <c>SizeToContent</c> 自适应大小，位置靠 <see cref="AnchorToKnob"/> 反算，
/// 让圆钮中心钉在屏幕上原来的地方。
/// <para/>
/// 交互上要同时容纳五件事，都是同一套手感：
/// <list type="bullet">
/// <item>按下后没怎么动就松开 → 展开 / 收起</item>
/// <item>移动超过阈值 → 转成拖窗口</item>
/// <item>右键，或触摸长按 → 设置菜单</item>
/// <item>展开后点某一条 → 执行那一条</item>
/// <item>指针移开、或闲置太久 → 自动收起</item>
/// </list>
/// </remarks>
public class ToolboxWindow : Window
{
    /// <summary>鼠标 / 笔：移动超过这么多逻辑像素就认为用户想拖窗口。</summary>
    /// <remarks>
    /// 8px 而不是更小：鼠标点击时手也会轻微移动，阈值太小的话「点一下」会被判成拖动，
    /// 窗口跟着挪几像素还不回来——这就是「点击变成了拖动」。
    /// </remarks>
    private const double MouseDragThreshold = 8.0;

    /// <summary>
    /// 触摸：要比手指按下的自然漂移大一截。
    /// </summary>
    /// <remarks>
    /// 触摸屏上「点一下」从来不是零位移——手指压下去、抬起来，中间会有十几像素的漂移。
    /// 阈值按鼠标那套来的话，绝大多数点击都会被判成拖动。
    /// <para/>
    /// 28 是量出来的折中：稳过常见的手指漂移（一般 5~20px），
    /// 又远小于有意识地拖动（想拖的时候手一定会走几十上百像素）。
    /// </remarks>
    private const double TouchDragThreshold = 28.0;

    /// <summary>
    /// 位移超过这个值就认为「不是在按住不动」，长按菜单取消。
    /// </summary>
    /// <remarks>
    /// 比拖动阈值小得多，是故意的：手指按下之后犹豫、轻微挪动的时候，
    /// 不该在 450ms 后突然弹出菜单——那也是一种「误触发」。
    /// </remarks>
    private const double HoldCancelDistance = 9.0;

    /// <summary>主按钮的上半行字——压在**上面那朵云**上。</summary>
    private const string KnobTopText = "教学";

    /// <summary>主按钮的下半行字——压在**下面那朵云**上。</summary>
    private const string KnobBottomText = "助手";

    #region 配色：天空

    // 一套固定的「云朵 + 天空」配色，不跟应用主题色走。
    // 天空的渐变和云朵由 CloudBackdrop 画在底层，这里只管描边、文字和按钮。
    //
    // 文字用带蓝的深灰而不是纯灰：压在淡蓝底色上，纯灰会显脏。
    private static readonly Color SkyBorder = Color.FromRgb(0x93, 0xC1, 0xEB);
    private static readonly Color SkyBorderStrong = Color.FromRgb(0x74, 0xAF, 0xE4);
    private static readonly Color SkyText = Color.FromRgb(0x3E, 0x54, 0x69);
    private static readonly Color SkyTextSoft = Color.FromRgb(0x7C, 0x94, 0xAB);

    /// <summary>
    /// 正在生效的批注/橡皮按钮的填充色。
    /// </summary>
    /// <remarks>
    /// 用"这颗云变暗"来表示"批注开着"，而不是给整个屏幕盖一层暗色——
    /// 后者会把课件一并压灰，而老师要的只是"知道现在是哪个工具在生效"。
    /// </remarks>
    private static readonly Color SkyItemActive = Color.FromArgb(0xFF, 0x8C, 0xA6, 0xC0);

    /// <summary>按钮的填充：接近纯白——按钮本身就是一朵朵小云，浮在蓝天上面。</summary>
    private static readonly Color SkyItemFill = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Color SkyItemHover = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>「需要确认」的暖色：白云里透出一点暖红，一眼能看出这条要当心。</summary>
    private static readonly Color SkyWarnBorder = Color.FromRgb(0xE2, 0x9B, 0x90);
    private static readonly Color SkyWarnFill = Color.FromArgb(0xF4, 0xFF, 0xF3, 0xF1);

    #endregion

    /// <summary>
    /// 主按钮上「上面那朵云」在云面里的位置（比例）。
    /// </summary>
    /// <remarks>
    /// 这组数字被两处共用：画云的 <c>Layers</c>，和给字定位的 <see cref="PlaceOnCloud"/>。
    /// 分成两份写迟早会对不上——字会飘到云外面去。
    /// </remarks>
    private static readonly Rect UpperCloud = new(0.24, 0.02, 0.78, 0.62);

    /// <summary>主按钮上「下面那朵云」在云面里的位置（比例）。</summary>
    private static readonly Rect LowerCloud = new(0.11, 0.34, 0.78, 0.62);

    /// <summary>
    /// 主按钮上那片云的画布是圆钮框的多少倍。
    /// </summary>
    /// <remarks>
    /// 1.34 是量出来的：两片等大的云要装下「教学助手」两行字，
    /// 1.0 倍时字号只能压到 0.17d（约 12px，太小）；1.34 倍能到 0.23d（约 16px），
    /// 又刚好不会和旁边的按钮撞上。
    /// </remarks>
    private const double KnobCloudScale = 1.34;

    /// <summary>触摸按住多久算长按。</summary>
    private const int HoldMilliseconds = 450;

    /// <summary>「需要确认」的快捷方式，第二次点击的有效期。</summary>
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(5);

    private readonly ToolboxSettings _settings;
    private readonly PickSettings _pick;
    private readonly RosterService _roster;
    private readonly PickStats _stats;

    private readonly Border _pill;
    private readonly Border _knob;
    private readonly TextBlock _knobLabelTop;
    private readonly TextBlock _knobLabelBottom;
    private readonly StackPanel _row;
    private readonly StackPanel _leftItems = new();
    private readonly StackPanel _rightItems = new();
    private readonly Border _messagePill;
    private readonly TextBlock _messageText;
    private readonly CloudBackdrop _pillBackdrop;
    private readonly CloudButtonSurface _knobBackdrop;
    private readonly StackPanel _root;
    private readonly Panel _annotateConfigLayer;

    /// <summary>配置栏的竖向容器：上行是工具和操作，下行是当前工具的参数。</summary>
    private readonly StackPanel _annotateRowsHost;

    /// <summary>上行：软笔 / 荧光笔 / 橡皮 / 激光笔 + 撤销 / 重做 / 清屏 / 退出。</summary>
    private readonly StackPanel _annotateToolRow;

    /// <summary>下行：当前工具的参数（颜色、粗细、橡皮档位）。激光笔没有参数，整行收起。</summary>
    private readonly StackPanel _annotateConfigRow;
    private readonly Panel _leftSlot = new();
    private readonly Panel _rightSlot = new();
    private readonly TextBlock _leftMessage;
    private readonly TextBlock _rightMessage;
    private readonly CloudBackdrop _messageBackdrop;
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _confirmTimer;

    /// <summary>自定义快捷方式对应的按钮，确认状态下要改它的外观。</summary>
    private readonly Dictionary<ShortcutItem, Border> _shortcutButtons = new();

    private MenuFlyout? _menu;
    private TopmostEnforcer? _topmost;

    private IPointer? _capturedPointer;
    private bool _pointerDown;
    private bool _dragging;
    private bool _menuOpened;
    private Point _pressOrigin;
    private PixelPoint _grabOffset;

    /// <summary>本次按下用哪个阈值（触摸和鼠标不一样）。</summary>
    private double _dragThreshold = MouseDragThreshold;

    private bool _expanded;
    private bool _repositionPending;

    /// <summary>正在显示「xxx 正在打开」那条消息（此时工具条的图标全部藏起来）。</summary>
    private bool _showingMessage;

    /// <summary>正在显示「打开快捷方式」的提示（只盖住快捷方式那一块，主按钮不动）。</summary>
    private bool _showingOpening;

    /// <summary>批注开着（画布已铺满屏幕）。</summary>
    private bool _annotateOn;

    /// <summary>批注的配置行是否展开。</summary>
    private bool _annotateRowsShown;

    /// <summary>触发这次批注的那颗按钮（配置栏要摆在它正上方）。</summary>
    private Control? _annotateSourceButton;

    /// <summary>工具条上那两颗批注按钮，用来就地更新明暗。</summary>
    private readonly List<(Border Chip, bool IsEraser)> _annotateButtons = [];

    /// <summary>正在摆配置栏。防止 LayoutUpdated 递归进去。</summary>
    private bool _positioningConfig;

    /// <summary>配置栏当前的横向偏移。用变换摆位，见 <see cref="PositionConfigRow"/>。</summary>
    private double _configOffset = double.NaN;


    /// <summary>圆钮中心在屏幕上的位置。展开时靠它把圆钮钉回原位。</summary>
    private PixelPoint _anchorCenter;

    /// <summary>正在等第二次点击确认的那条快捷方式。</summary>
    private ShortcutItem? _pendingConfirm;
    private Border? _pendingConfirmButton;

    public ToolboxWindow(ToolboxSettings settings, RosterService roster, PickStats stats)
    {
        _settings = settings;
        _pick = settings.Pick;
        _roster = roster;
        _stats = stats;

        SystemDecorations = SystemDecorations.None;
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        // 内容多大窗口就多大：收起时正好是个圆，展开时正好是一条。
        SizeToContent = SizeToContent.WidthAndHeight;

        // 四个字**分压在两朵云上**：上面那朵两个字、下面那朵两个字。
        // 所以是两个 TextBlock，各自定位到自己的那朵云上（见 PlaceOnCloud）。
        _knobLabelTop = BuildKnobLabel(KnobTopText);
        _knobLabelBottom = BuildKnobLabel(KnobBottomText);

        // 主按钮：**两片部分重叠的云**，上下两行，各画各的轮廓。
        // 没有外圈的圆形/胶囊底——之前那版把几个椭圆并成一整块，看着像套了个圆形背景。
        _knobBackdrop = new CloudButtonSurface
        {
            // **两片等大的云，一片左下、一片右上**，中间有一段重叠。
            // 后面那片（右上）淡一点，前面那片（左下）实心——才看得出是两片。
            //
            // 下面那朵**在水平方向是居中的**：它是承载文字的那一朵，
            // 偏一点字就跟着偏，整个主按钮看着就不正。错落感交给上面那朵去表达。
            Layers =
            [
                new CloudLayer(UpperCloud.X, UpperCloud.Y, UpperCloud.Width, UpperCloud.Height, 0.92),
                new CloudLayer(LowerCloud.X, LowerCloud.Y, LowerCloud.Width, LowerCloud.Height, 1.00)
            ],
            Fill = Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF),
            Stroke = SkyBorderStrong,
            StrokeThickness = 1.6
        };

        _knob = new Border
        {
            Background = null,
            BorderThickness = new Thickness(0),
            Child = new Panel { Children = { _knobBackdrop, _knobLabelTop, _knobLabelBottom } },
            Cursor = new Cursor(StandardCursorType.Hand),
            RenderTransform = TransformOperations.Parse("scale(1)"),
            RenderTransformOrigin = RelativePoint.Center,
            Transitions =
            [
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(110),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                }
            ]
        };

        foreach (var panel in new[] { _leftItems, _rightItems })
        {
            panel.Orientation = Orientation.Horizontal;
            panel.Spacing = 8;
            panel.VerticalAlignment = VerticalAlignment.Center;
            panel.IsVisible = false;
            panel.Opacity = 0;
            panel.RenderTransformOrigin = RelativePoint.Center;

            // 展开动画挂在按钮组自己身上，**不碰窗口尺寸**。
            // 位移用 translate 而不是改宽度：translate 只影响绘制、不影响布局，
            // 所以窗口的尺寸和位置在第一帧就定死了，圆钮不会被动画带着抖。
            panel.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(170),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(190),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                }
            ];
        }

        // 打开快捷方式时的提示：只盖住快捷方式那一块，主按钮原样不动。
        // 所以提示是**分左右两条**的——左边那条右对齐、右边那条左对齐，
        // 两边都往中间（也就是主按钮两侧）靠，读起来是连贯的一句。
        _leftMessage = new TextBlock
        {
            Foreground = new SolidColorBrush(SkyText),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            IsVisible = false
        };

        _rightMessage = new TextBlock
        {
            Foreground = new SolidColorBrush(SkyTextSoft),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false
        };

        // 每一侧是一个"槽"：平时放按钮组，打开快捷方式时换成一条提示。
        // 用 Panel 叠着，切换时只是换谁可见——主按钮始终在原位，锚点逻辑一行都不用改。
        _leftSlot = new Panel { Children = { _leftItems, _leftMessage } };
        _rightSlot = new Panel { Children = { _rightItems, _rightMessage } };

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _leftSlot, _knob, _rightSlot }
        };

        // 整条底板的云和天。内边距不能挂在 Border 上：Border 的 Padding 会把子元素
        // （连同这层底）一起缩进去，天空就填不满整块了。改成给 _row 留外边距。
        // 背景板只是一片薄薄的蓝天，**不画云**：
        // 高度取主按钮的一半、贴着底边，于是「按钮一半落在板上、一半悬在空中」——
        // 这样既像云飘在天上，遮住底下的内容也少得多。
        _pillBackdrop = new CloudBackdrop
        {
            CornerRadius = 999,
            ShowClouds = false,
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        _row.Margin = new Thickness(9, 0, 9, 0);

        _pill = new Border
        {
            Background = null,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(0),
            // 别拉伸：批注配置栏比工具条宽的时候窗口会变宽，
            // 主行跟着撑开的话，蓝天底板会变得比按钮宽出一大截。
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Panel { Children = { _pillBackdrop, _row } }
        };

        _messageText = new TextBlock
        {
            Foreground = new SolidColorBrush(SkyText),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };

        _messageBackdrop = new CloudBackdrop { CornerRadius = 999, CloudOpacity = 0.8 };

        _messagePill = new Border
        {
            Background = null,
            BorderBrush = new SolidColorBrush(SkyBorderStrong),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(0),
            Child = new Panel
            {
                Children =
                {
                    _messageBackdrop,
                    new Border
                    {
                        Padding = new Thickness(18, 10, 18, 10),
                        Child = _messageText
                    }
                }
            },
            IsVisible = false
        };

        // 工具条和消息同时只显示一个，所以叠在一个 Panel 里就够了：
        // 谁可见，窗口就自动是那个的大小。
        // 配置行加在主行**上面**。
        // 没有单独的"工具栏行"了——批注和橡皮本来就是工具条上的两颗按钮，
        // 再放一行"软笔/橡皮"是重复的。这一行只放当前工具的配置。
        // 主按钮的位置由锚点逻辑钉住，所以窗口长高之后它会自动留在原地。
        // 配置栏要落在**被点的那颗按钮正上方**，好让人一眼看出这行是给谁的。
        //
        // 用 Panel 而不是 Canvas 承载：**Canvas 的 DesiredSize 是 0**，不占任何高度，
        // 子元素会直接压到下一行（主行）上去。Panel 会按子元素算高度，位置则用 Margin 摆。
        // 宽度显式设成主行的宽度并裁剪，这样配置栏比工具条宽时也不会把窗口撑开。
        _annotateConfigRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // 上行：工具 + 操作。和下行一样居中——两行宽度不一样时，
        // 窄的那行居中比左对齐好看得多。
        _annotateToolRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // 两行套一个竖向容器：Panel 不会自动把子元素排开，直接塞两个进去会重叠。
        _annotateRowsHost = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { _annotateToolRow, _annotateConfigRow }
        };

        // 不裁剪：内容超出显示范围时被切掉正是之前的毛病。
        // 宽度改成取「主行」和「配置栏」的较大者，宁可窗口宽一点，也不切内容。
        _annotateConfigLayer = new Panel { IsVisible = false };
        _annotateConfigLayer.Children.Add(_annotateRowsHost);

        _root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                _annotateConfigLayer,
                new Panel { Children = { _pill, _messagePill } }
            }
        };

        Content = _root;

        ApplyAccent();
        ApplySize();
        RefreshItems();

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMilliseconds) };
        _holdTimer.Tick += OnHoldElapsed;

        _confirmTimer = new DispatcherTimer { Interval = ConfirmWindow };
        _confirmTimer.Tick += (_, _) => ClearPendingConfirm();

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerEntered += OnPointerEntered;
        PositionChanged += (_, _) => ClampToScreen();
        SizeChanged += OnSizeChanged;

        _roster.RosterChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshItems();
        });
    }

    /// <summary>用户点了「幸运抽签」。</summary>
    public event EventHandler? PickRequested;

    /// <summary>用户要看幸运抽签统计（饼状图）。</summary>
    public event EventHandler? StatsRequested;

    /// <summary>设置被菜单改动，需要持久化。</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>用户要求隐藏工具条。</summary>
    public event EventHandler? HideRequested;

    /// <summary>批注状态变了（开关、换笔、换橡皮、清屏）。宿主据此复位置顶并落盘。</summary>
    public event EventHandler? AnnotateChanged;

    /// <summary>
    /// 重新抢一次置顶。
    /// </summary>
    /// <remarks>
    /// 批注层刚铺上来的时候会盖在工具条上面，得把工具条再顶回去——
    /// 否则批注按钮被透明纸挡住，点不动，也就关不掉了。
    /// </remarks>
    public void ReassertTopmost() => _topmost?.Reassert();

    #region 外观

    /// <summary>
    /// 强调色。
    /// </summary>
    /// <remarks>
    /// 工具条本身用的是固定的「天空」配色，不跟应用主题色走；
    /// 这个属性留给弹出的「正在打开」消息和中央大字用——那些偶尔出现，
    /// 跟一下主题色反而更容易被认出来。
    /// </remarks>
    public Color Accent { get; private set; } = SkyBorderStrong;

    public void ApplyAccent()
    {
        if (Application.Current is { } app &&
            app.TryFindResource("SystemAccentColor", app.ActualThemeVariant, out var value))
        {
            Accent = value switch
            {
                Color c => c,
                SolidColorBrush b => b.Color,
                _ => Accent
            };
        }

        // 底板和圆钮的描边是固定的淡蓝，不随主题色变。
        _knob.BorderBrush = new SolidColorBrush(SkyBorderStrong);
        _pill.BorderBrush = new SolidColorBrush(SkyBorder);
    }

    /// <summary>按当前尺寸档位刷新窗口与圆钮的大小。</summary>
    public void ApplySize()
    {
        var d = _settings.Diameter;
        // 框的大小 = 云面的大小（见 KnobBox）。云面不再溢出框，
        // 窗口也就装得下整朵云——之前"主按钮依旧截断"就是云比框大、被窗口边缘切掉了。
        _knob.Width = KnobBox;
        _knob.Height = KnobBox;
        _knob.CornerRadius = new CornerRadius(KnobBox / 2);

        // 云面 = 框（见 KnobBox）：两片等大的云要把「教学助手」四个字分开装下，
        // 1:1 的旧框太小，字号只能压到 0.17d（约 12px）。框整体放大 1.34 倍之后能到 0.22d。
        _knobBackdrop.Width = KnobBox;
        _knobBackdrop.Height = KnobBox;
        _knobBackdrop.HorizontalAlignment = HorizontalAlignment.Center;
        _knobBackdrop.VerticalAlignment = VerticalAlignment.Center;

        // 每行字各自定位到**它所压的那朵云的"头"中心**。
        var fontSize = d * 0.22;

        _knobLabelTop.FontSize = fontSize;
        _knobLabelBottom.FontSize = fontSize;

        PlaceOnCloud(_knobLabelTop, UpperCloud, KnobBox);
        PlaceOnCloud(_knobLabelBottom, LowerCloud, KnobBox);

        // 横向留出内边距；纵向不留——纵向靠"按钮居中 + 板子贴底半高"来对齐，
        // 一旦加了纵向 margin，按钮中心就不再正好落在板子的上沿，悬空比例会变。
        _row.Margin = new Thickness(d * 0.18, 0, d * 0.18, 0);
        ApplyOpacity();

        // 板子高度 = 主按钮的一半。按钮是纵向居中的，于是它的中心正好压在板子上沿，
        // 上下各露一半——「一半在板上、一半悬空」是算出来的，不是凑出来的。
        // 底板高 = 行高的一半：行高就是主按钮的框高，而按钮是纵向居中的，
        // 于是按钮中心正好压在底板上沿，上下各露一半。
        _pillBackdrop.Height = KnobBox / 2;

        _messageText.FontSize = d * 0.215;
        if (_messagePill.Child is Panel { Children: [_, Border padded] })
        {
            padded.Padding = new Thickness(d * 0.28, d * 0.15, d * 0.28, d * 0.15);
        }

        RefreshItems();
        _repositionPending = true;
        ClampToScreen();
    }

    /// <summary>
    /// 重建两侧的按钮。
    /// </summary>
    /// <remarks>
    /// 顺序取自 <see cref="ToolboxSettings.EffectiveOrder"/>——内置功能和快捷方式混在一起排，
    /// 谁在前谁在后是用户拖出来的。
    /// 然后从中间劈开：<b>左边放一半，右边放一半；总数是奇数时右边多一个</b>
    /// （<c>count / 2</c> 向下取整，剩下的都归右边）。
    /// </remarks>
    public void RefreshItems()
    {
        _leftItems.Children.Clear();
        _rightItems.Children.Clear();
        _shortcutButtons.Clear();
        _annotateButtons.Clear();

        var all = new List<Control>();

        // 按用户排好的顺序铺。内置功能和自定义快捷方式一视同仁——
        // 「幸运抽签」也可以被拖到任何位置，不再钉死在最前面。
        foreach (var key in _settings.EffectiveOrder())
        {
            if (key == ToolboxSettings.LuckyDrawKey)
            {
                all.Add(BuildIconButton("🙋", "抽人", null, SkyBorderStrong, null,
                    () => PickRequested?.Invoke(this, EventArgs.Empty)));
                continue;
            }

            if (key is ToolboxSettings.AnnotateKey or ToolboxSettings.EraserKey)
            {
                // 批注和橡皮是**两颗独立按钮**：想擦就直接点橡皮，不用先进菜单换工具。
                // 打开着的那一颗下面显示「已开」，不用去看屏幕才知道当前是笔还是橡皮。
                var isEraser = key == ToolboxSettings.EraserKey;
                // 比"类"不比具体工具：用荧光笔或激光笔时，「批注」那颗也要亮着。
                var active = _annotateOn && IsEraserFamily(_settings.AnnotateTool) == isEraser;

                Border? chip = null;
                chip = BuildIconButton(
                    isEraser ? "\U0001F9FC" : "\u270F\uFE0F",
                    isEraser ? "橡皮" : "批注",
                    null, SkyBorderStrong, null, () => ToggleAnnotate(isEraser, chip));

                _annotateButtons.Add((chip, isEraser));
                all.Add(chip);
                continue;
            }

            var shortcut = _settings.Shortcuts.FirstOrDefault(x => x.Id == key);
            if (shortcut is not null)
            {
                all.Add(CreateShortcutButton(shortcut));
            }
        }

        // 正在生效的那一颗**变暗**：一眼能看出现在是笔还是橡皮，不用去看屏幕。
        // 必须在列表建好之后就地改，而不是重建按钮——重建会换掉控件，
        // 配置栏"摆在哪颗按钮上方"用的那个引用就失效了。
        UpdateAnnotateButtons();

        var leftCount = all.Count / 2;
        for (var i = 0; i < all.Count; i++)
        {
            (i < leftCount ? _leftItems : _rightItems).Children.Add(all[i]);
        }

        UpdateItemVisibility();

        // 正在展开的时候东西被删光了（比如刚关掉幸运抽签又没加快捷方式）：
        // 顺手收起来，别留一条空荡荡的底板在屏幕上。
        if (_expanded && all.Count == 0)
        {
            SetExpanded(false);
        }
    }

    /// <summary>工具条上现在有没有可点的东西。</summary>
    private bool HasAnyItem => _leftItems.Children.Count > 0 || _rightItems.Children.Count > 0;

    /// <summary>
    /// 刷新两侧「按钮组 / 打开提示」的显隐。
    /// </summary>
    /// <remarks>
    /// 展开状态、有没有内容、是不是正在打开快捷方式，这三个条件一起决定谁露脸。
    /// 集中在一个地方算，免得几处调用点各写各的、互相打架。
    /// </remarks>
    private void UpdateItemVisibility()
    {
        var opening = _showingOpening;

        _leftItems.IsVisible = !opening && _leftItems.Children.Count > 0;
        _rightItems.IsVisible = !opening && _rightItems.Children.Count > 0;

        _leftMessage.IsVisible = opening;
        _rightMessage.IsVisible = opening;

        // 槽本身只在展开时才出现；提示态即使一条快捷方式都没有也要露出来。
        _leftSlot.IsVisible = _expanded && (opening || _leftItems.Children.Count > 0);
        _rightSlot.IsVisible = _expanded && (opening || _rightItems.Children.Count > 0);
    }

    /// <summary>内置按钮：图标 + 主标题 + 可选小字。</summary>
    private Border BuildIconButton(string icon, string title, string? subtitle, Color accent,
        Bitmap? fileIcon, Action onClick)
    {
        var width = MeasureButtonWidth(title, subtitle);
        return BuildButtonShell(
            BuildIconNameContent(icon, title, subtitle, fileIcon), accent, onClick, width);
    }

    /// <summary>
    /// 用户自定义的快捷方式按钮：图标 + 名称。
    /// </summary>
    /// <remarks>
    /// 图标优先用<b>文件本身的图标</b>（<see cref="FileIconLoader"/>）——
    /// 用户选了 `chrome.exe`，按钮上就该是 Chrome 的图标，而不是一个通用的小齿轮。
    /// 取不到（网址、ClassIsland 地址、文件不在、或者不在 Windows 上）
    /// 再退回用户填的图标 / 类型默认图标。
    /// </remarks>
    private Border CreateShortcutButton(ShortcutItem shortcut)
    {
        var color = shortcut.Confirm ? SkyWarnBorder : SkyBorder;
        var fileIcon = shortcut.UseFileIcon ? FileIconLoader.Load(shortcut.Target) : null;
        var width = MeasureButtonWidth(shortcut.ButtonText, null);

        var border = BuildButtonShell(
            BuildIconNameContent(shortcut.IconOrDefault, shortcut.ButtonText, null, fileIcon),
            color,
            () => InvokeShortcut(shortcut),
            width);

        _shortcutButtons[shortcut] = border;
        return border;
    }

    /// <summary>
    /// 量一个按钮要多宽。
    /// </summary>
    /// <remarks>
    /// <b>按钮宽度必须在建的时候就定死。</b>点「需要确认」的按钮时标题会从「关机」变成
    /// 「再点一次」，如果宽度跟着内容走，整条工具条会当场重排——圆钮和别的按钮一起平移，
    /// 看着就是「鬼畜」。所以这里按「正式标题」和「再点一次」里更宽的那个算，
    /// 顺带把内置按钮的小字也算进去。
    /// </remarks>
    private double MeasureButtonWidth(string name, string? subtitle)
    {
        var h = _settings.ItemHeight;
        var nameFont = h * 0.215;

        var content = Math.Max(MeasureText(name, nameFont), MeasureText("再点一次", nameFont));
        if (!string.IsNullOrEmpty(subtitle))
        {
            content = Math.Max(content, MeasureText(subtitle, h * 0.155));
        }

        // 图标那一行最宽也就一个字宽，按字号留够即可。
        content = Math.Max(content, MeasureText("M", h * 0.34));

        return Math.Max(h, content + h * 0.62);
    }

    /// <summary>量一段文字要多宽。量不出来就按「一个字符约等于一个字号」粗估。</summary>
    private static double MeasureText(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, fontSize, Brushes.White);
            return formatted.Width;
        }
        catch (Exception)
        {
            // 取不到字体度量时宁可估宽一点：宽了只是留白，窄了文字会被切。
            return text.Length * fontSize;
        }
    }

    /// <summary>
    /// 按钮里的内容：上面一个大图标，下面一行名称，再下面可选的极小字。
    /// </summary>
    /// <remarks>
    /// 按钮高度是固定的（跟着圆钮走），所以字号全部按
    /// <see cref="ToolboxSettings.ItemHeight"/> 折算，换尺寸档位时不用逐处调整。
    /// <para/>
    /// 图标用 emoji 而不是 Fluent 图标字形：字形要靠宿主那边的字体，
    /// 换到 Linux / macOS 上就是一个方框。用户也可以填一个汉字（比如「投」「网」），
    /// 那样任何中文字体都能渲染。
    /// </remarks>
    private Control BuildIconNameContent(string icon, string name, string? subtitle, Bitmap? fileIcon)
    {
        var h = _settings.ItemHeight;

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (fileIcon is not null)
        {
            // 有文件图标就用它。尺寸写死，免得不同 dpi 的图标把按钮撑成不同高矮。
            stack.Children.Add(new Image
            {
                Source = fileIcon,
                Width = h * 0.44,
                Height = h * 0.44,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = icon,
                FontSize = h * 0.34,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = new SolidColorBrush(SkyText),
            FontWeight = FontWeight.SemiBold,
            FontSize = h * 0.215,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        });

        if (!string.IsNullOrEmpty(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = new SolidColorBrush(SkyTextSoft),
                FontSize = h * 0.155,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
        }

        return stack;
    }

    /// <summary>
    /// 所有按钮共用的外壳：圆角胶囊 + 悬停 / 按下反馈。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="Border"/> 而不是 <see cref="Button"/>：宿主只挂了 FluentAvalonia 的主题，
    /// <c>Button</c> 的模板会带来一堆用不上的视觉状态，想改成这种「工具条胶囊」反而要跟它打架。
    /// Border 上自己处理指针事件，行为完全可控，也不依赖任何主题资源。
    /// </remarks>
    private Border BuildButtonShell(Control content, Color accent, Action onClick, double width)
    {
        var height = _settings.ItemHeight;

        // 轮廓本身就是一朵云，填充和描边都交给它画；Border 只剩下"占位 + 接收事件"的壳。
        var surface = new CloudButtonSurface
        {
            Fill = SkyItemFill,
            Stroke = accent,
            StrokeThickness = 1.4
        };

        var border = new Border
        {
            Height = height,
            // 宽度写死，标题变了也不会重排——见 MeasureButtonWidth 的说明。
            Width = width,
            Padding = new Thickness(0),
            Background = null,
            BorderThickness = new Thickness(0),
            Child = new Panel { Children = { surface, content } },
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };

        border.PointerEntered += (_, _) => surface.SetFill(SkyItemHover);
        border.PointerExited += (_, _) => surface.SetFill(SkyItemFill);
        border.PointerPressed += (_, e) =>
        {
            // 不让事件冒到窗口上去，否则点按钮会被当成「点圆钮」而把工具条收起来。
            e.Handled = true;
        };
        border.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return border;
    }

    #endregion

    #region 展开 / 收起

    /// <summary>当前是不是展开状态。</summary>
    public bool IsExpanded => _expanded;

    /// <summary>
    /// 展开或收起工具条。
    /// </summary>
    /// <remarks>
    /// 只切两侧按钮组的显隐，不动 <c>_row</c> 的子级顺序——
    /// 所以圆钮永远待在正中间，展开前后它自己不会跳。
    /// </remarks>
    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        if (expanded && !HasAnyItem)
        {
            // 一个功能都没有的时候展开出来是一条空底板，看着像坏了。
            // 直接给一句话说明白。
            _ = ShowEmptyHintAsync();
            return;
        }

        if (expanded)
        {
            // 必须在改布局之前记：这时窗口还是收起的，位置就是圆钮的位置。
            CaptureAnchor();
        }

        _expanded = expanded;
        ClearPendingConfirm();

        if (expanded)
        {
            // 先把内容摆成「透明 + 向中间收着」，再让 RefreshItems 把它们显示出来。
            PrepareItemsForFadeIn();

            RefreshItems();

            // 下一帧才把目标值写回去，过渡才会跑。
            // 这一帧窗口已经按最终尺寸摆好位置了——所以是「窗口先到位，内容再浮现」，
            // 顺序反过来的话就会看到圆钮先跳一下再出按钮。
            Dispatcher.UIThread.Post(FadeItemsIn, DispatcherPriority.Render);
        }
        else
        {
            _leftItems.Opacity = 0;
            _rightItems.Opacity = 0;
        }

        UpdateItemVisibility();
        UpdateAnnotateRows();

        _repositionPending = true;
        InvalidateMeasure();
        SettleLayoutAndAnchor();
    }

    /// <summary>
    /// 强制跑一次布局，然后立刻把窗口摆正——让尺寸和位置在**同一帧**里定下来。
    /// </summary>
    /// <remarks>
    /// 不这么做的话，Avalonia 要到下一轮布局才更新窗口尺寸，
    /// 中间会有一帧是「新尺寸 + 旧位置」：整条工具条先按旧位置画宽了一截，下一帧才摆正。
    /// 在高刷屏上是一闪而过，在触摸屏 / 低帧率下就是肉眼可见的「点一下整条跳一下」。
    /// <para/>
    /// <see cref="Layoutable.UpdateLayout"/> 在布局过程中被重入调用会抛异常，
    /// 那种情况下直接放弃，交给 <c>SizeChanged</c> 兜底——结果一样，只是晚一帧。
    /// </remarks>
    private void SettleLayoutAndAnchor()
    {
        try
        {
            UpdateLayout();
        }
        catch (Exception)
        {
            return;
        }

        // 先立刻摆一次，消掉"新尺寸 + 旧位置"的中间帧。
        Reposition();

        // 但**不能**把标记清掉：UpdateLayout 有可能在尺寸还没真正落地时就被调用
        // （比如窗口刚改可见性），那一次摆的位置是错的。
        // 留着标记，等真正的 SizeChanged 来了再摆一次——这个操作是幂等的，
        // 多摆一次没有任何副作用，少摆一次就是肉眼可见的偏移。
        _repositionPending = true;
    }

    /// <summary>展开前：让两侧按钮组「透明 + 向中间收着」。</summary>
    private void PrepareItemsForFadeIn()
    {
        var offset = _settings.ItemHeight * 0.28;
        _leftItems.Opacity = 0;
        _rightItems.Opacity = 0;
        _leftItems.RenderTransform = TransformOperations.Parse($"translateX({offset.ToString(CultureInfo.InvariantCulture)}px)");
        _rightItems.RenderTransform = TransformOperations.Parse($"translateX({(-offset).ToString(CultureInfo.InvariantCulture)}px)");
    }

    /// <summary>展开后：淡入并滑到位。两侧从中间往两边推开，视觉上和「展开」这个动作同向。</summary>
    private void FadeItemsIn()
    {
        if (!_expanded)
        {
            return;
        }

        _leftItems.Opacity = 1;
        _rightItems.Opacity = 1;
        _leftItems.RenderTransform = TransformOperations.Parse("translateX(0px)");
        _rightItems.RenderTransform = TransformOperations.Parse("translateX(0px)");
    }

    /// <summary>
    /// 取一个控件当前的宽度：布局跑过就用实际宽度，没跑过就用期望宽度。
    /// </summary>
    /// <remarks>
    /// <c>Bounds</c> 要等 arrange 之后才有值；如果只跑了 measure（或者根本没跑），
    /// 它是 0。这时用 <c>DesiredSize</c> 顶上，位置才不会算歪。
    /// </remarks>
    private static double VisibleWidth(Control control)
    {
        if (!control.IsVisible)
        {
            return 0;
        }

        return control.Bounds.Width > 0 ? control.Bounds.Width : control.DesiredSize.Width;
    }

    /// <summary>
    /// 按<b>当前形态</b>把圆钮的中心位置记成锚点。
    /// </summary>
    /// <remarks>
    /// <b>不能在展开时跳过。</b>拖动是展开、收起都能做的，
    /// 如果展开时拖动完不更新锚点，下一次收起时 <see cref="AnchorToKnob"/> 会拿旧锚点算，
    /// 整条就"弹回上一次的位置"——展开状态下永远拖不走。
    /// <para/>
    /// 圆钮在窗口里的偏移量靠 <see cref="KnobBounds"/> 算，它本身就会考虑展开时左侧那组按钮的宽度，
    /// 所以这里对两种形态都成立。
    /// </remarks>
    private void CaptureAnchor()
    {
        // 消息态下圆钮不在窗口里，锚点保持原样。
        if (_showingMessage)
        {
            return;
        }

        var scaling = CurrentScaling;
        var knob = KnobBounds;

        // 横向的偏移和纵向的偏移是两回事，别共用一个数。
        _anchorCenter = new PixelPoint(
            Position.X + (int)Math.Round((knob.X + knob.Width / 2) * scaling),
            Position.Y + (int)Math.Round((knob.Y + knob.Height / 2) * scaling));
    }

    /// <summary>
    /// 主按钮那个框有多大。
    /// </summary>
    /// <remarks>
    /// <b>云面就是这么大，不再超出框。</b>之前云面按 1.34 倍画在框外，而窗口尺寸是按框算的——
    /// 多出来的那圈直接被窗口边缘切平，看着就是"主按钮被截断"
    /// （上面那朵云顶上平、下面那朵云底下平）。现在框本身放大了 1.34 倍，云和字都在框里。
    /// <para/>
    /// 代价是「主按钮大小」这个设置的实际视觉尺寸比数值大 1.34 倍——数值控制的是框，云把框填满了。
    /// </remarks>
    private double KnobBox => _settings.Diameter * KnobCloudScale;

    /// <summary>底板的横向内边距（逻辑像素）。</summary>
    private double Pad => _row.Margin.Left;

    /// <summary>
    /// 底板的纵向内边距。
    /// </summary>
    /// <remarks>
    /// 现在是 0：纵向靠「按钮居中 + 板子贴底半高」对齐，加了纵向内边距，
    /// 按钮中心就不再正好落在板子上沿，悬空比例会变。
    /// 留这个属性是为了让横纵两套算法各算各的，不互相借。
    /// </remarks>
    private double PadV => _row.Margin.Top;

    private double CurrentScaling
    {
        get
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            return screen is { Scaling: > 0 } ? screen.Scaling : 1.0;
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_repositionPending)
        {
            _repositionPending = false;
            Reposition();
        }

        ClampToScreen();
    }

    /// <summary>按当前形态摆位置：正常态让圆钮不动，消息态让消息居中在圆钮原来的地方。</summary>
    private void Reposition()
    {
        if (_showingMessage)
        {
            AnchorMessageToAnchor();
        }
        else
        {
            AnchorToKnob();
        }

        // 配置栏和主按钮一样，都是"读过布局才敢算"的位置，所以放在同一处算。
        // 挂在 LayoutUpdated 上是不行的：写布局属性又会触发 LayoutUpdated，
        // 一转起来 Avalonia 直接抛 Infinite layout loop。
        PositionConfigRow();
    }

    /// <summary>
    /// 把消息胶囊摆到圆钮原来的位置上（居中）。
    /// </summary>
    /// <remarks>
    /// 消息比圆钮宽得多，如果还按「左上角对齐」摆，它会整条往右甩出去。
    /// 按中心对齐，视觉上就是「从圆钮的位置长出来一条」，位置不跳。
    /// </remarks>
    private void AnchorMessageToAnchor()
    {
        var scaling = CurrentScaling;
        var width = (int)Math.Round(Bounds.Width * scaling);
        var height = (int)Math.Round(Bounds.Height * scaling);

        Position = new PixelPoint(
            _anchorCenter.X - width / 2,
            _anchorCenter.Y - height / 2);

        ClampToScreen();
    }

    /// <summary>
    /// 重新摆窗口，让圆钮的中心停在 <see cref="_anchorCenter"/> 不动。
    /// </summary>
    /// <remarks>
    /// 直接算「圆钮中心在窗口里偏了多少」，再拿锚点减掉它——
    /// 这样不管两侧各放了几条、宽度差多少，圆钮都会稳稳待在原地，
    /// 也不需要为「往左展开 / 往右展开」写两套逻辑。
    /// </remarks>
    private void AnchorToKnob()
    {
        var scaling = CurrentScaling;

        // **只认 KnobBounds 这一个来源。**
        // 以前这里手算了一遍「内边距 + 左侧按钮组宽度 + 半径」，而 CaptureAnchor 用的是
        // KnobBounds——两套算法一旦不一致，主按钮就会在展开/拖动时跳。前后踩过三次
        // （展开时上跳 166px、拖动后弹回、改背景板后横移 12px），全是同一个原因。
        // 现在两边共用同一个矩形，这类 bug 从根上没有了。
        var knob = KnobBounds;

        Position = new PixelPoint(
            _anchorCenter.X - (int)Math.Round((knob.X + knob.Width / 2) * scaling),
            _anchorCenter.Y - (int)Math.Round((knob.Y + knob.Height / 2) * scaling));

        ClampToScreen();
    }

    #endregion

    #region 位置

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (_settings.WindowX == int.MinValue || _settings.WindowY == int.MinValue)
        {
            if (screen is not null)
            {
                var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                var size = (int)Math.Ceiling(_settings.Diameter * scaling);
                Position = new PixelPoint(
                    screen.WorkingArea.X + screen.WorkingArea.Width - size - (int)(48 * scaling),
                    screen.WorkingArea.Y + screen.WorkingArea.Height - size - (int)(48 * scaling));
            }
        }
        else
        {
            Position = new PixelPoint(_settings.WindowX, _settings.WindowY);
        }

        ClampToScreen();
        CaptureAnchor();
        ApplyAccent();

        _topmost = new TopmostEnforcer(this);
        _topmost.Attach();
    }

    /// <summary>
    /// 把窗口夹回当前屏幕内。
    /// </summary>
    /// <remarks>
    /// 用的是 <c>Screen.Bounds</c> 而不是 <c>WorkingArea</c>——前者含任务栏区域，
    /// 也就是允许盖住任务栏，但不允许拖出屏幕。
    /// <para/>
    /// 展开后靠边的工具条会被这里推回屏幕内，此时圆钮会跟着偏一点。
    /// 这是刻意的：宁可圆钮挪一下，也好过有一半按钮在屏幕外面够不着。
    /// </remarks>
    private void ClampToScreen()
    {
        var screen = Screens.ScreenFromWindow(this)
                     ?? Screens.ScreenFromPoint(Position)
                     ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);

        var maxX = Math.Max(bounds.X, bounds.X + bounds.Width - width);
        var maxY = Math.Max(bounds.Y, bounds.Y + bounds.Height - height);
        var x = Math.Clamp(Position.X, bounds.X, maxX);
        var y = Math.Clamp(Position.Y, bounds.Y, maxY);

        if (x != Position.X || y != Position.Y)
        {
            Position = new PixelPoint(x, y);
        }
    }

    /// <summary>
    /// 把当前位置写回设置。
    /// </summary>
    /// <remarks>
    /// 存的是<b>收起状态下的窗口左上角</b>，不是当前左上角——
    /// 展开时窗口比圆钮宽，直接存当前左上角的话，下次启动会以那个位置当收起位置摆，
    /// 圆钮就跑到旁边去了。所以统一从锚点（圆钮中心）反推。
    /// </remarks>
    public void CapturePosition()
    {
        CaptureAnchor();

        var scaling = CurrentScaling;
        var offsetX = (int)Math.Round((Pad + KnobBox / 2) * scaling);
        var offsetY = (int)Math.Round((PadV + KnobBox / 2) * scaling);

        _settings.WindowX = _anchorCenter.X - offsetX;
        _settings.WindowY = _anchorCenter.Y - offsetY;
    }

    #endregion

    #region 交互

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            ShowMenu();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 已经有一个手指在操作了：第二根手指按下不该把状态搅乱。
        if (_pointerDown)
        {
            return;
        }

        // 点在展开出来的按钮上时，按钮自己会处理，这里不接管。
        if (_expanded && !IsOnKnob(point.Position))
        {
            return;
        }

        _pointerDown = true;
        _dragging = false;
        _menuOpened = false;
        _pressOrigin = point.Position;
        _dragThreshold = e.Pointer.Type is PointerType.Touch or PointerType.Pen
            ? TouchDragThreshold
            : MouseDragThreshold;

        var pressOnScreen = this.PointToScreen(point.Position);
        _grabOffset = new PixelPoint(pressOnScreen.X - Position.X, pressOnScreen.Y - Position.Y);

        _knob.RenderTransform = TransformOperations.Parse("scale(0.93)");
        e.Pointer.Capture(this);
        _capturedPointer = e.Pointer;

        // 触摸屏上没有右键，用长按代替。鼠标按住不动很常见，弹菜单会很意外。
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            _holdTimer.Start();
        }

        e.Handled = true;
    }

    /// <summary>
    /// 圆钮在窗口里的矩形。展开后它夹在两组按钮中间，所以不能用固定坐标。
    /// </summary>
    /// <remarks>
    /// <b>横向和纵向的内边距是分开算的。</b>这个坑已经踩过三次了：
    /// 横向偏移里含左侧按钮组的宽度，纵向永远没有——两者共用一个数，
    /// 结果就是展开时整条上下跳，或者拖动之后回到错误的位置。
    /// </remarks>
    private Rect KnobBounds
    {
        get
        {
            // 分两段算，别混在一起：
            //   ① 行**内部**的偏移——左边那组按钮的宽度 + 一个间距；
            //   ② _row 在**窗口里**的偏移——它自己的外边距（Pad），外加可能压在上面的批注两行。
            // 以前把 ① 里也塞了一个 Pad，等于算了两遍，一加批注行就露馅。
            var leftWidth = VisibleWidth(_leftSlot);
            var innerX = leftWidth + (leftWidth > 0 ? _row.Spacing : 0);
            var offset = _row.TranslatePoint(new Point(0, 0), this) ?? default;

            return new Rect(
                innerX + offset.X, offset.Y,
                KnobBox, KnobBox);
        }
    }

    private bool IsOnKnob(Point point) => KnobBounds.Contains(point);

    private void OnHoldElapsed(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (!_pointerDown || _dragging)
        {
            return;
        }

        _menuOpened = true;
        _knob.RenderTransform = TransformOperations.Parse("scale(1)");
        ShowMenu();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown || _menuOpened)
        {
            return;
        }

        var current = e.GetPosition(this);
        var dx = current.X - _pressOrigin.X;
        var dy = current.Y - _pressOrigin.Y;

        var distance = Math.Sqrt(dx * dx + dy * dy);

        // 只要动了一点，就不再算「按住不动」，长按菜单取消。
        // 这一条比拖动阈值松得多，是为了避免「手指按下后犹豫了一下」被弹菜单打断。
        if (distance > HoldCancelDistance)
        {
            _holdTimer.Stop();
        }

        if (distance < _dragThreshold)
        {
            return;
        }

        if (!_dragging)
        {
            _dragging = true;

            // 起步这一下不能按「绝对定位」直接跟过去：指针已经离按下点一个阈值那么远了，
            // 直接跟会让窗口"啪"地跳一格。这里把跨过阈值之前的那段位移扣掉，
            // 只走超出的部分——既不跳，也不会因为触摸驱动把多个事件合并成一个而整段丢掉。
            var ratio = Math.Max(0, distance - _dragThreshold) / distance;
            Position = new PixelPoint(
                Position.X + (int)Math.Round(dx * ratio),
                Position.Y + (int)Math.Round(dy * ratio));

            // 重新对基准，之后就是普通的跟手拖动。
            var pressed = this.PointToScreen(current);
            _grabOffset = new PixelPoint(pressed.X - Position.X, pressed.Y - Position.Y);

            // 手感和反馈：从「按下」的缩小变回略微放大，一眼能看出现在是拖动而不是点击。
            _knob.RenderTransform = TransformOperations.Parse("scale(1.06)");
            ClampToScreen();
            e.Handled = true;
            return;
        }

        var pointerOnScreen = this.PointToScreen(current);
        Position = new PixelPoint(
            pointerOnScreen.X - _grabOffset.X,
            pointerOnScreen.Y - _grabOffset.Y);
        ClampToScreen();
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        _holdTimer.Stop();
        _pointerDown = false;
        _knob.RenderTransform = TransformOperations.Parse("scale(1)");
        e.Pointer.Capture(null);
        _capturedPointer = null;

        if (_menuOpened)
        {
            _menuOpened = false;
        }
        else if (_dragging)
        {
            // 判定一旦成立就不再翻案：是拖动就保持拖动后的位置。
            //
            // 之前这里还有一道「总位移不够就退回原位并当成点击」的二次判定，
            // 那正是「互相误触发」的来源——用户明明在拖，松手却变成了点击，
            // 工具条莫名其妙展开/收起。距离阈值一旦跨过，意图就已经明确了。
            _dragging = false;
            CaptureAnchor();
            CapturePosition();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.InitialPressMouseButton is MouseButton.Left or MouseButton.None)
        {
            SetExpanded(!_expanded);
        }

        e.Handled = true;
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        // 指针进进出出不改变展开状态：工具条只在「点圆钮」和「点完快捷方式且开了执行后收起」时才收。
    }

    #endregion

    #region 快捷方式

    private async void InvokeShortcut(ShortcutItem shortcut)
    {
        if (shortcut.Confirm && !ReferenceEquals(_pendingConfirm, shortcut))
        {
            ArmConfirm(shortcut);
            return;
        }

        ClearPendingConfirm();

        // 只把快捷方式那一块换成提示，主按钮原样留在正中间。
        // 用户既能看到「点到了、正在打开」，主按钮也还在原地可以接着点。
        BeginOpening(shortcut.Name);

        // 先让消息真的画出来再动手：Process.Start 偶尔会卡几百毫秒，
        // 顺序反过来的话用户先看到的是"点击毫无反应"。
        await Task.Delay(120);

        var error = shortcut.Kind == ShortcutKind.ClassIslandUri
            // 导航必须回 UI 线程做
            ? ShortcutRunner.Run(shortcut)
            // 起进程/开文件交给线程池，别把界面冻住
            : await Task.Run(() => ShortcutRunner.Run(shortcut));

        if (error is not null)
        {
            BeginOpenFailed(shortcut.Name, error);
            await Task.Delay(2400);
        }
        else
        {
            await Task.Delay(900);
        }

        EndOpening();

        // 用快捷方式**不收起**工具条——只有点主按钮才会收。
    }

    /// <summary>
    /// 进入「正在打开」提示态。
    /// </summary>
    /// <remarks>
    /// 提示分左右两条，分别盖在原先两组按钮的位置上：
    /// 左边那条右对齐、右边那条左对齐，都往中间靠，读起来连成一句
    /// 「浏览器 正在打开 ．(主按钮)． 请稍后…」。
    /// <b>主按钮不参与</b>——它一直待在那儿，位置和样子都不变。
    /// </remarks>
    private void BeginOpening(string name)
    {
        _showingOpening = true;

        SetMessage(_leftMessage, $"{name} 正在打开", SkyText);
        SetMessage(_rightMessage, "请稍后…", SkyTextSoft);

        RefreshVisibilityAndAnchor();
    }

    /// <summary>打开失败：两条提示都换成暖红，右边那条放原因。</summary>
    private void BeginOpenFailed(string name, string error)
    {
        _showingOpening = true;

        SetMessage(_leftMessage, $"{name} 打开失败", SkyWarnBorder);
        SetMessage(_rightMessage, error, SkyWarnBorder);

        RefreshVisibilityAndAnchor();
    }

    /// <summary>退出提示态，把快捷方式按钮换回来。</summary>
    private void EndOpening()
    {
        if (!_showingOpening)
        {
            return;
        }

        _showingOpening = false;
        RefreshVisibilityAndAnchor();
    }

    private static void SetMessage(TextBlock block, string text, Color color)
    {
        block.Text = text;
        block.Foreground = new SolidColorBrush(color);
    }

    /// <summary>切完显隐之后重新摆一次位置——提示和按钮宽度不一样，得让主按钮钉在原地。</summary>
    private void RefreshVisibilityAndAnchor()
    {
        UpdateItemVisibility();

        _repositionPending = true;
        InvalidateMeasure();
        SettleLayoutAndAnchor();
    }

    /// <summary>
    /// 进入「正在打开」形态：工具条的图标全部藏起来，只显示一条消息。
    /// </summary>
    /// <remarks>
    /// 消息胶囊的宽度和工具条完全不一样，所以位置要重新按锚点居中摆一次
    /// （见 <see cref="AnchorMessageToAnchor"/>），不然会从圆钮那儿往右甩出去。
    /// </remarks>
    private void BeginOpeningMessage(string text, bool warn = false)
    {
        _showingMessage = true;

        _messageText.Text = text;
        _messageText.Foreground = new SolidColorBrush(warn ? SkyWarnBorder : SkyText);
        _messagePill.BorderBrush = new SolidColorBrush(warn ? SkyWarnBorder : SkyBorderStrong);

        _row.IsVisible = false;
        _messagePill.IsVisible = true;

        _repositionPending = true;
        InvalidateMeasure();
        SettleLayoutAndAnchor();
    }

    /// <summary>一个功能都没有的时候，用消息条说明一下，而不是展开成一条空的。</summary>
    private async Task ShowEmptyHintAsync()
    {
        if (_showingMessage)
        {
            return;
        }

        BeginOpeningMessage("还没有可用的功能，请到设置页添加");
        await Task.Delay(2600);
        EndOpeningMessage();
    }

    /// <summary>退出「正在打开」形态，把工具条恢复出来。</summary>
    private void EndOpeningMessage()
    {
        if (!_showingMessage)
        {
            return;
        }

        _showingMessage = false;
        _messagePill.IsVisible = false;
        _row.IsVisible = true;

        _repositionPending = true;
        InvalidateMeasure();
        SettleLayoutAndAnchor();
    }

    /// <summary>
    /// 给「需要确认」的快捷方式上膛。
    /// </summary>
    /// <remarks>
    /// 工具条常驻桌面，误触成本很高（比如「关机」）。第一次点击只改按钮外观，
    /// 几秒内点第二次才真的执行，超时自动取消。比弹对话框轻，也不打断操作。
    /// </remarks>
    private void ArmConfirm(ShortcutItem shortcut)
    {
        ClearPendingConfirm();

        _pendingConfirm = shortcut;

        if (_shortcutButtons.TryGetValue(shortcut, out var button))
        {
            _pendingConfirmButton = button;
            if (FindNameText(button) is { } text)
            {
                text.Text = "再点一次";
            }

            SurfaceOf(button)?.SetStroke(SkyWarnBorder);
            SurfaceOf(button)?.SetFill(SkyWarnFill);
        }

        _confirmTimer.Stop();
        _confirmTimer.Start();
    }

    private void ClearPendingConfirm()
    {
        _confirmTimer.Stop();

        if (_pendingConfirmButton is not null && _pendingConfirm is { } item &&
            FindNameText(_pendingConfirmButton) is { } text)
        {
            text.Text = item.ButtonText;

            var surface = SurfaceOf(_pendingConfirmButton);
            surface?.SetFill(SkyItemFill);
            surface?.SetStroke(SkyBorder);
        }

        _pendingConfirm = null;
        _pendingConfirmButton = null;
    }

    /// <summary>从按钮壳里取出那朵云（填充和描边都归它管）。</summary>
    private static CloudButtonSurface? SurfaceOf(Control? button) =>
        (button as Border)?.Child is Panel panel
            ? panel.Children.OfType<CloudButtonSurface>().FirstOrDefault()
            : null;

    /// <summary>
    /// 在「图标 + 名称」的内容里找出名称那一个 TextBlock。
    /// </summary>
    /// <remarks>
    /// 按钮壳里现在叠了两层（云朵底层 + 内容），所以要先穿过那层 Panel 找到内容面板。
    /// 直接当成 <c>Border.Child is StackPanel</c> 判断的话，确认态就改不到文字了。
    /// </remarks>
    private static TextBlock? FindNameText(Control button) =>
        ContentPanelOf(button)?.Children.OfType<TextBlock>().ElementAtOrDefault(1);

    /// <summary>取出按钮里的「图标 + 名称」内容面板。</summary>
    /// <remarks>
    /// 注意判断顺序：<see cref="StackPanel"/> 是 <see cref="Panel"/> 的子类，
    /// 先匹配 Panel 的话第二个分支就是死代码，而且内容面板本身会被当成"外层"。
    /// </remarks>
    private static StackPanel? ContentPanelOf(Control? button)
    {
        if (button is not Border { Child: Control child })
        {
            return null;
        }

        return child is StackPanel direct
            ? direct
            : child is Panel layered
                ? layered.Children.OfType<StackPanel>().FirstOrDefault()
                : null;
    }

    #endregion

    #region 设置菜单

    private const string SizeGroup = "toolbox.size";

    /// <summary>
    /// 弹出设置菜单。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="MenuFlyout"/> 而不是 <c>ContextMenu</c>：ClassIsland 的控件主题只来自
    /// FluentAvalonia，它不提供 <c>ContextMenu</c> 的 ControlTheme，
    /// 弹出来是个 0×0 的空窗口，表现就是「右键没反应」。
    /// </remarks>
    private void ShowMenu()
    {
        // 触摸长按时指针还被窗口捕获着，捕获期间事件不会进弹出层，先放掉。
        _capturedPointer?.Capture(null);
        _capturedPointer = null;

        _menu = _menu ?? new MenuFlyout();
        _menu.ItemsSource = BuildMenuItems();
        _menu.Closed += (_, _) => { };
        _menu.ShowAt(_knob, showAtPointer: true);
    }

    private object[] BuildMenuItems()
    {
        var total = _roster.Names.Count;

        var items = new List<object>
        {
            Header(_settings.EnableLuckyDraw
                ? $"教学助手 · 第 {_stats.Rounds} 轮 · 共 {total} 人"
                : "教学助手")
        };

        // 幸运抽签关掉之后，菜单里跟它相关的几项也一起藏掉，免得点了没反应。
        if (_settings.EnableLuckyDraw)
        {
            items.Add(Item("幸运抽签", () => PickRequested?.Invoke(this, EventArgs.Empty)));
            items.Add(new Separator());
            items.Add(Item("查看幸运抽签次数（饼状图）", () => StatsRequested?.Invoke(this, EventArgs.Empty)));
            items.Add(Item("开始新一轮", () =>
            {
                _roster.ResetRound(_pick, _stats);
                RefreshItems();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }));
            items.Add(Item("打开名单文件", OpenRosterFile));
            items.Add(Item("重新载入名单", () =>
            {
                _roster.Reload();
                RefreshItems();
            }));
            items.Add(new Separator());
        }

        items.Add(Item(_expanded ? "收起工具条" : "展开工具条", () => SetExpanded(!_expanded)));
        items.Add(new MenuItem
        {
            // 菜单里做不了滑动条，给三个常用档位；要细调去设置页拖滑块。
            Header = "主按钮大小",
            ItemsSource = new object[]
            {
                Choice("小", SizeGroup, IsDiameter(52), () => SetDiameter(52)),
                Choice("中", SizeGroup, IsDiameter(68), () => SetDiameter(68)),
                Choice("大", SizeGroup, IsDiameter(88), () => SetDiameter(88))
            }
        });
        items.Add(new Separator());
        items.Add(Item("隐藏工具条", () => HideRequested?.Invoke(this, EventArgs.Empty)));

        return [.. items];
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Choice(string header, string groupName, bool isChecked, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.Radio,
            GroupName = groupName,
            IsChecked = isChecked
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Header(string text) => new() { Header = text, IsEnabled = false };

    #region 屏幕批注

    /// <summary>
    /// 点「批注」或「橡皮」按钮。
    /// </summary>
    /// <param name="eraser">点的是不是橡皮那一颗。</param>
    /// <remarks>
    /// 交互：<b>按一次开启 → 再按打开配置栏 → 再按收起配置栏 → 再按又打开……</b>
    /// 也就是开启之后，同一颗按钮就是"配置栏的开关键"。
    /// <para/>
    /// 关掉批注不走这里，走配置栏里的 <b>✖ 退出</b>——
    /// 那样能保证"关掉"是一个明确的动作，不会在按配置栏的时候误触退出。
    /// <para/>
    /// 在笔和橡皮之间切换时，配置栏**保持打开**：刚换过去通常就是想调一下颜色或粗细。
    /// </remarks>
    private void ToggleAnnotate(bool eraser, Control? source = null)
    {
        if (source is not null)
        {
            _annotateSourceButton = source;
        }

        // 工具条上那两颗按钮各自对应一支工具：批注 → 软笔，橡皮 → 橡皮。
        var wanted = eraser ? AnnotateTool.Eraser : AnnotateTool.Pen;

        if (!_annotateOn)
        {
            _annotateOn = true;
            _annotateRowsShown = false;
            _settings.AnnotateTool = wanted;

            AnnotationWindow.ShowOverlay();
            AnnotationWindow.ResumeOverlay();
        }
        else if (IsEraserFamily(_settings.AnnotateTool) != IsEraserFamily(wanted))
        {
            // 换的是"哪一类"（笔 ↔ 橡皮），配置栏保持打开，正好接着调。
            _settings.AnnotateTool = wanted;
            _annotateRowsShown = true;
        }
        else
        {
            _annotateRowsShown = !_annotateRowsShown;
        }

        PushAnnotateSettings();
        UpdateAnnotateButtons();
        UpdateAnnotateRows();
        AnnotateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 工具条上那颗按钮代表的"类"：橡皮，还是笔。
    /// </summary>
    /// <remarks>
    /// 工具条只放「批注」和「橡皮」两颗按钮，而笔这一类下面还有荧光笔和激光笔。
    /// 用荧光笔时点亮的应该是「批注」——所以比的是类，不是具体哪一支。
    /// </remarks>
    private static bool IsEraserFamily(AnnotateTool tool) => tool == AnnotateTool.Eraser;

    /// <summary>
    /// 退出批注。
    /// </summary>
    /// <remarks>
    /// <b>不清屏</b>：画的内容留着，下次进来还在。
    /// 关掉覆盖窗之后触屏就还给系统了——批注期间那一层是全屏窗口，会把触摸全吃掉。
    /// </remarks>
    private void ExitAnnotate()
    {
        _annotateOn = false;
        _annotateRowsShown = false;

        // 只"暂停"：画留在屏幕上，鼠标和触屏还给系统。
        // 直接关窗口会把画一起丢掉，再进来就是白纸——那才是"退出就清屏"。
        AnnotationWindow.PauseOverlay();

        // 激光轨迹是"正在指给人看"的东西，退出之后留着没有意义。
        AnnotationWindow.Canvas?.ClearLaser();
        UpdateAnnotateButtons();
        UpdateAnnotateRows();
        AnnotateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把当前工具和它的参数推给画布。</summary>
    private void PushAnnotateSettings()
    {
        if (AnnotationWindow.Canvas is not { } canvas)
        {
            return;
        }

        var width = PenWidthAt(_settings.AnnotatePenSize);

        canvas.Tool = _settings.AnnotateTool;
        canvas.PenColor = ParseColor(_settings.AnnotatePenColor, Color.FromRgb(0xFF, 0x3B, 0x30));
        canvas.PenThickness = width;
        canvas.HighlightColor = ParseColor(_settings.AnnotateHighlightColor, Color.FromRgb(0xFF, 0xEB, 0x3B));
        canvas.HighlightThickness = width * AnnotationPalette.HighlighterWidthFactor;
        canvas.EraserRadius = AnnotationPalette.EraserRadii[
            Math.Clamp(_settings.AnnotateEraserSize, 0, AnnotationPalette.EraserRadii.Count - 1)];

        // 从激光笔换走时把它留下的轨迹抹掉，不然那串点会一直挂到过期为止。
        if (_settings.AnnotateTool != AnnotateTool.Laser)
        {
            canvas.ClearLaser();
        }
    }

    /// <summary>第 <paramref name="index"/> 档的笔迹粗细（逻辑像素）。</summary>
    /// <remarks>
    /// 用**系数乘主按钮直径**而不是绝对像素：主按钮调大之后整条工具条等比放大，
    /// 笔迹也该跟着粗一点，不然在大屏上会显得太细。
    /// </remarks>
    private double PenWidthAt(int index) =>
        Math.Max(2.0, _settings.Diameter * AnnotationPalette.PenWidthFactors[
            Math.Clamp(index, 0, AnnotationPalette.PenWidthFactors.Count - 1)]);

    private static Color ParseColor(string? hex, Color fallback) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var parsed) ? parsed : fallback;

    /// <summary>
    /// 刷新配置栏的两行。
    /// </summary>
    /// <remarks>
    /// <b>上行是「用哪支工具 + 几个动作」，下行是「这支工具的参数」。</b>
    /// 之所以分成两行：工具从两支变成四支之后一行塞不下——
    /// 四支工具 + 撤销/重做/清屏/退出 + 六个颜色 + 三个粗细挤成一排就没法看了。
    /// 鸿合、希沃那类电子教鞭也都是这么分的：上面选工具，下面调参数。
    /// </remarks>
    private void UpdateAnnotateRows()
    {
        _annotateConfigLayer.IsVisible = _annotateRowsShown && _expanded;
        _annotateToolRow.Children.Clear();
        _annotateConfigRow.Children.Clear();

        // ---- 上行：四支工具 ----
        AddToolChip(AnnotateTool.Pen, AnnotationPalette.PenToolDot, "软笔");
        AddToolChip(AnnotateTool.Highlighter, AnnotationPalette.HighlighterToolDot, "荧光笔");
        AddToolChip(AnnotateTool.Eraser, AnnotationPalette.EraserToolDot, "橡皮");
        AddToolChip(AnnotateTool.Laser, AnnotationPalette.LaserToolDot, "激光笔");

        // ---- 上行：动作。撤销/重做没得可撤时置灰——
        //      点了没反应比按钮变灰更让人以为是坏了。
        var canvas = AnnotationWindow.Canvas;
        _annotateToolRow.Children.Add(BuildAnnotateChip(AnnotationPalette.UndoDot, "撤销", false,
            UndoAnnotate, canvas?.CanUndo ?? false));
        _annotateToolRow.Children.Add(BuildAnnotateChip(AnnotationPalette.RedoDot, "重做", false,
            RedoAnnotate, canvas?.CanRedo ?? false));
        _annotateToolRow.Children.Add(BuildAnnotateChip(AnnotationPalette.ClearDot, "清屏", false, ClearAnnotate));
        // 退出放在最后：擦除不清屏，只是把批注层收掉、触屏还给系统。
        _annotateToolRow.Children.Add(BuildAnnotateChip(AnnotationPalette.ExitDot, "退出", false, ExitAnnotate));

        // ---- 下行：当前工具的参数 ----
        switch (_settings.AnnotateTool)
        {
            case AnnotateTool.Eraser:
                for (var i = 0; i < AnnotationPalette.EraserRadii.Count; i++)
                {
                    var size = i;
                    _annotateConfigRow.Children.Add(BuildAnnotateChip(
                        EraserIcon(i), null, _settings.AnnotateEraserSize == size, () =>
                        {
                            _settings.AnnotateEraserSize = size;
                            PushAnnotateSettings();
                            UpdateAnnotateRows();
                            AnnotateChanged?.Invoke(this, EventArgs.Empty);
                        }));
                }

                break;

            case AnnotateTool.Laser:
                // 激光笔没有可调参数，下行留空——下面那行会把它收掉。
                break;

            case AnnotateTool.Highlighter:
                AddColorChips(AnnotationPalette.Highlighters, _settings.AnnotateHighlightColor,
                    hex => _settings.AnnotateHighlightColor = hex);
                AddWidthChips();
                break;

            default:
                AddColorChips(AnnotationPalette.Pens, _settings.AnnotatePenColor,
                    hex => _settings.AnnotatePenColor = hex);
                AddWidthChips();
                break;
        }

        // 空的下行不占位置：激光笔模式下配置栏就只有一行高。
        _annotateConfigRow.IsVisible = _annotateConfigRow.Children.Count > 0;

        // 这里只画内容；摆位置交给 Reposition（它会先 UpdateLayout 再算），
        // 因为换完内容之后窗口还要再量一次，现在算出来的坐标是过期的。
        _repositionPending = true;
        InvalidateMeasure();
    }

    private void AddToolChip(AnnotateTool tool, string icon, string label) =>
        _annotateToolRow.Children.Add(BuildAnnotateChip(
            icon, label, _settings.AnnotateTool == tool, () => SelectAnnotateTool(tool)));

    private void SelectAnnotateTool(AnnotateTool tool)
    {
        if (_settings.AnnotateTool == tool)
        {
            return;
        }

        _settings.AnnotateTool = tool;
        PushAnnotateSettings();
        UpdateAnnotateButtons();
        UpdateAnnotateRows();
        AnnotateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>铺一排颜色，当前选中的那个高亮。</summary>
    private void AddColorChips(
        IReadOnlyList<(string Name, string Hex, string Dot)> palette, string current, Action<string> pick)
    {
        foreach (var (_, hex, _) in palette)
        {
            var picked = hex;
            var active = string.Equals(current, picked, StringComparison.OrdinalIgnoreCase);
            _annotateConfigRow.Children.Add(BuildAnnotateChip(PenIcon(hex), null, active, () =>
            {
                pick(picked);
                PushAnnotateSettings();
                UpdateAnnotateRows();
                AnnotateChanged?.Invoke(this, EventArgs.Empty);
            }));
        }
    }

    /// <summary>铺一排粗细档位。</summary>
    private void AddWidthChips()
    {
        for (var i = 0; i < AnnotationPalette.PenWidthFactors.Count; i++)
        {
            var size = i;
            _annotateConfigRow.Children.Add(BuildAnnotateChip(
                WidthIcon(i), null, _settings.AnnotatePenSize == size, () =>
                {
                    _settings.AnnotatePenSize = size;
                    PushAnnotateSettings();
                    UpdateAnnotateRows();
                    AnnotateChanged?.Invoke(this, EventArgs.Empty);
                }));
        }
    }

    private void UndoAnnotate()
    {
        AnnotationWindow.Canvas?.Undo();
        UpdateAnnotateRows();
        AnnotateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RedoAnnotate()
    {
        AnnotationWindow.Canvas?.Redo();
        UpdateAnnotateRows();
        AnnotateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearAnnotate()
    {
        // Clear 内部会记一步历史，所以清错了还能撤销回来。
        AnnotationWindow.Canvas?.Clear();
        UpdateAnnotateRows();
        AnnotateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 笔的颜色图标：**画一个实心圆**。
    /// </summary>
    /// <remarks>
    /// 不用 🔴🟡 这类 emoji：颜色选择器最要紧的就是"颜色看得见"，
    /// 而 emoji 依赖彩色字体——系统缺字时它们会变成黑白符号，甚至是一坨斜纹，
    /// 那样按钮就完全失去意义了。画出来的圆在任何环境下都是对的颜色。
    /// </remarks>
    private Control PenIcon(string hex)
    {
        var size = _settings.ItemHeight * 0.40;
        var color = Color.TryParse(hex, out var parsed) ? parsed : Colors.Red;

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(color),
            // 白色描一圈，白笔在浅色底上也能看出边界。
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x49, 0x5E)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>橡皮的图标：一个方块，**大小对应该档的擦除范围**。</summary>
    private Control EraserIcon(int sizeIndex)
    {
        var full = _settings.ItemHeight * 0.42;
        var scale = 0.5 + sizeIndex * 0.25;   // 小 / 中 / 大

        return new Border
        {
            Width = full * scale,
            Height = full * scale,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x3E, 0x54, 0x69)),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// 粗细档位的图标：一个圆点，**大小对应该档笔迹有多粗**。
    /// </summary>
    /// <remarks>
    /// 和 <see cref="EraserIcon"/> 一样用"看着大小就知道"的表达，而不是写「细/中/粗」三个字——
    /// 字要在课上眯着眼读，图形不用。用圆的不用方的，是为了和橡皮的方图标一眼区分开。
    /// </remarks>
    private Control WidthIcon(int sizeIndex)
    {
        var full = _settings.ItemHeight * 0.42;
        var scale = 0.45 + sizeIndex * 0.275;

        return new Border
        {
            Width = full * scale,
            Height = full * scale,
            CornerRadius = new CornerRadius(full * scale / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x2B, 0x42, 0x57)),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// 刷新「批注 / 橡皮」两颗按钮的明暗。
    /// </summary>
    /// <remarks>
    /// 就地改填充，不重建按钮：重建会换掉控件对象，
    /// 而配置栏靠"记住是哪颗按钮被点了"来决定摆在哪，引用一断位置就没了。
    /// </remarks>
    private void UpdateAnnotateButtons()
    {
        foreach (var (chip, isEraser) in _annotateButtons)
        {
            // 比的是"类"：正在用荧光笔或激光笔时，「批注」那颗也该是亮的。
            var active = _annotateOn && IsEraserFamily(_settings.AnnotateTool) == isEraser;
            SurfaceOf(chip)?.SetFill(active ? SkyItemActive : SkyItemFill);
        }
    }

    /// <summary>批注行上的一个小按钮。选中的那个用更深的描边和底色标出来。</summary>
    /// <param name="enabled">
    /// 置灰的按钮点不动，也不参与悬停高亮。撤销/重做在没得可撤时用得上——
    /// 能点但没反应，比直接变灰更让人以为是坏了。
    /// </param>
    private Control BuildAnnotateChip(string icon, string? label, bool active, Action onClick, bool enabled = true) =>
        BuildAnnotateChip(new TextBlock
        {
            Text = icon,
            FontSize = _settings.ItemHeight * 0.72 * 0.42,
            VerticalAlignment = VerticalAlignment.Center
        }, label, active, onClick, enabled);

    private Control BuildAnnotateChip(Control icon, string? label, bool active, Action onClick, bool enabled = true)
    {
        var height = _settings.ItemHeight * 0.72;

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = height * 0.10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        content.Children.Add(icon);

        if (label is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = height * 0.30,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(SkyText),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var surface = new CloudButtonSurface
        {
            Fill = active ? Color.FromArgb(0xFF, 0xE3, 0xF2, 0xFF) : SkyItemFill,
            Stroke = active ? SkyBorderStrong : SkyBorder,
            StrokeThickness = active ? 2.0 : 1.2
        };

        // 留白加在内容上，不加在 Border 上：Border 的 Padding 会把云一起挤窄，
        // 云就变成一条细长的竖条，而不是一颗按钮。
        content.Margin = new Thickness(height * 0.22, 0, height * 0.22, 0);

        var chip = new Border
        {
            Height = height,
            MinWidth = height,
            Padding = new Thickness(0),
            Background = null,
            BorderThickness = new Thickness(0),
            Child = new Panel { Children = { surface, content } },
            Cursor = new Cursor(enabled ? StandardCursorType.Hand : StandardCursorType.Arrow),
            Opacity = enabled ? 1.0 : 0.38,
            VerticalAlignment = VerticalAlignment.Center
        };

        chip.PointerEntered += (_, _) =>
        {
            if (enabled)
            {
                surface.SetFill(SkyItemHover);
            }
        };
        chip.PointerExited += (_, _) =>
        {
            if (enabled)
            {
                surface.SetFill(active ? Color.FromArgb(0xFF, 0xE3, 0xF2, 0xFF) : SkyItemFill);
            }
        };
        chip.PointerReleased += (_, e) =>
        {
            if (enabled && e.InitialPressMouseButton == MouseButton.Left)
            {
                onClick();
            }
        };

        return chip;
    }

    #endregion

    /// <summary>
    /// 把悬浮窗透明度作用到根内容上。
    /// </summary>
    /// <remarks>
    /// 作用在根内容而不是 <c>Window.Opacity</c>：后者依赖平台的窗口合成，
    /// 有的环境下会整个失效；作用在内容上就是普通的渲染期 alpha，各处表现一致。
    /// </remarks>
    public void ApplyOpacity()
    {
        _root.Opacity = Math.Clamp(_settings.ToolbarOpacity,
            ToolboxSettings.MinOpacity, ToolboxSettings.MaxOpacity);
    }

    /// <summary>
    /// 把配置栏摆到被点的那颗按钮正上方。
    /// </summary>
    /// <remarks>
    /// 只改横向位置，纵向仍然紧贴主行；超出两端就贴边，不会把窗口撑宽。
    /// </remarks>
    private void PositionConfigRow()
    {
        if (_annotateSourceButton is null || !_annotateConfigLayer.IsVisible)
        {
            return;
        }

        // 只在值真的变了的时候写布局属性，免得平白多跑一轮布局。
        if (_positioningConfig)
        {
            return;
        }

        _positioningConfig = true;

        // 用**布局落定后的实际宽度**，不要用 DesiredSize。
        // 刚换完内容的文字首次测量会偏大（实测 356 vs 排完的 311），
        // 拿它去夹位置会把配置栏推到边上、对不上按钮。
        var configWidth = _annotateRowsHost.Bounds.Width;
        if (configWidth <= 1)
        {
            _positioningConfig = false;
            return;
        }

        // 层宽度取「主行」和「配置栏」里大的那个：配置栏比工具条宽时窗口整体变宽，
        // 而不是把配置栏切掉。主行是居中的，不会跟着变宽，所以不会来回抖。
        var layerWidth = Math.Max(_pill.Bounds.Width, configWidth);
        if (Math.Abs(_annotateConfigLayer.Width - layerWidth) > 0.5)
        {
            _annotateConfigLayer.Width = layerWidth;
        }

        var layerOrigin = _annotateConfigLayer.TranslatePoint(new Point(0, 0), this) ?? default;
        var buttonOrigin = _annotateSourceButton.TranslatePoint(new Point(0, 0), this) ?? default;
        var buttonCenter = buttonOrigin.X - layerOrigin.X + _annotateSourceButton.Bounds.Width / 2;

        var left = Math.Clamp(buttonCenter - configWidth / 2,
            0, Math.Max(0, layerWidth - configWidth));

        // 用**变换**摆位，不用 Margin：变换不参与布局，
        // 也就不会"摆了位置 → 布局变 → 再摆"地转圈。
        if (double.IsNaN(_configOffset) || Math.Abs(_configOffset - left) > 0.5)
        {
            _configOffset = left;
            _annotateRowsHost.RenderTransform = new TranslateTransform(left, 0);
        }

        _positioningConfig = false;
    }

    private static TextBlock BuildKnobLabel(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(SkyText),
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.NoWrap
    };

    /// <summary>
    /// 把一行字摆到某朵云的"头"中心。
    /// </summary>
    /// <remarks>
    /// 云是四个椭圆拼的，其中中间那个大"头"是视觉重心，字压在它中心最稳。
    /// 用 RenderTransform 平移而不是 Margin：只挪渲染、不动布局，
    /// 也就不会反过来影响主按钮的锚点计算。
    /// </remarks>
    private static void PlaceOnCloud(TextBlock label, Rect cloud, double box)
    {
        // 云面 == 框，所以直接从比例换算就行，不用再补居中偏移。
        var centerX = (cloud.X + cloud.Width / 2) * box;
        var centerY = (cloud.Y + 0.39 * cloud.Height) * box;

        label.RenderTransform = new TranslateTransform(centerX - box / 2, centerY - box / 2);
        label.Margin = new Thickness(0);
    }

    /// <summary>当前直径是不是（差不多）这个值。菜单里用来打勾。</summary>
    private bool IsDiameter(double value) => Math.Abs(_settings.Diameter - value) < 1;

    /// <summary>
    /// 换主按钮直径。
    /// </summary>
    /// <remarks>
    /// 先收起再换：尺寸一变，两侧按钮的宽度全变了，
    /// 带着展开态去算锚点会算歪，主按钮会跳。
    /// </remarks>
    private void SetDiameter(double diameter)
    {
        var clamped = Math.Clamp(diameter, ToolboxSettings.MinDiameter, ToolboxSettings.MaxDiameter);
        if (Math.Abs(_settings.Diameter - clamped) < 0.01)
        {
            return;
        }

        var wasExpanded = _expanded;

        SetExpanded(false);
        _settings.Diameter = clamped;
        ApplySize();
        CaptureAnchor();
        CapturePosition();

        if (wasExpanded)
        {
            SetExpanded(true);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenRosterFile()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_roster.RosterPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 没有关联程序就算了。
        }
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _holdTimer.Stop();
        _confirmTimer.Stop();
        _menu?.Hide();
        _topmost?.Dispose();
        base.OnClosed(e);
    }
}
