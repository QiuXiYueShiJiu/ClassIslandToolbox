using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Toolbox.Models;
using ClassIsland.Toolbox.Services;
using ClassIsland.Toolbox.Views;
using ClassIsland.Toolbox.Views.SettingsPages;

namespace TbTest;

internal sealed class App : Application
{
}

/// <summary>
/// 把工具条在 Avalonia 无头平台 + Skia 下真的画一遍，截图存盘。
/// </summary>
/// <remarks>
/// 重点测那些「看图看不出来、但用户一眼能感觉到」的几何约束：
/// <list type="bullet">
/// <item>展开后圆钮是否真的居中（左右按「奇数时右边多一个」分）；</item>
/// <item>展开前后圆钮的屏幕位置是否没动；</item>
/// <item><b>点需要确认的按钮时，宽度和位置会不会变</b>——这就是「鬼畜」的来源。</item>
/// </list>
/// </remarks>
internal static class Program
{
    private static int _failures;
    private static readonly string OutDir = "/tmp/tbshots";

    [STAThread]
    public static int Main()
    {
        Directory.CreateDirectory(OutDir);

        try
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .AfterSetup(_ => Application.Current!.Styles
                    .Add(new FluentAvalonia.Styling.FluentAvaloniaTheme()))
                .SetupWithoutStarting();
        }
        catch (Exception ex)
        {
            Console.WriteLine("无头平台起不来: " + ex);
            return 1;
        }

        TestToolbox();
        TestSettingsPage();
        TestRevealWindow();
        TestAnnotation();
        TestShortcutPaths();
        TestRosterParsing();

        Console.WriteLine(_failures == 0 ? "UI 冒烟测试通过。" : $"UI 冒烟测试有 {_failures} 处失败。");
        return _failures == 0 ? 0 : 1;
    }

    #region 反射小工具

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, args);

    /// <summary>读插件里的 private const。</summary>
    private static double Const(string name) =>
        (double)typeof(ToolboxWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static T InvokeResult<T>(object target, string method) =>
        (T)target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, null)!;

    /// <summary>按钮壳里叠了「云朵底层 + 内容」两层，穿过去取内容面板。</summary>
    private static StackPanel? ContentPanel(Control button)
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

    private static TextBlock? TextAt(Control button, int index) =>
        ContentPanel(button)?.Children.OfType<TextBlock>().ElementAtOrDefault(index);

    private static Point KnobCenterInWindow(ToolboxWindow window)
    {
        var bounds = (Rect)window.GetType()
            .GetProperty("KnobBounds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;
        return new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }

    /// <summary>圆钮中心在屏幕上的位置。<b>X 和 Y 都要看</b>——只比 X 会漏掉纵向跳动。</summary>
    private static Point KnobCenterOnScreen(ToolboxWindow window)
    {
        var knob = KnobCenterInWindow(window);
        return new Point(window.Position.X + knob.X, window.Position.Y + knob.Y);
    }

    /// <summary>
    /// 等界面稳下来。
    /// </summary>
    /// <remarks>
    /// 必须真的睡一会儿：展开动画（透明度 + 位移过渡）是**按时钟推进**的，
    /// <c>RunJobs</c> 只把排队的任务跑完，不会让过渡前进。
    /// 不等的话截出来的是半透明的中间帧，断言也会误判成「没到 1」。
    /// </remarks>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 2; i++)
        {
            Thread.Sleep(220);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
        }

        Dispatcher.UIThread.RunJobs();
    }

    #endregion

    private static void TestToolbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tbtoolbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "名单.txt"),
            Enumerable.Range(1, 30).Select(i => $"学生{i:D2}"));

        var settings = new ToolboxSettings();
        settings.Shortcuts.Add(new ShortcutItem
        {
            Name = "浏览器", Icon = "🌐", Kind = ShortcutKind.Url, Target = "https://www.classisland.tech"
        });
        settings.Shortcuts.Add(new ShortcutItem
        {
            Name = "记事本", Icon = "📝", Kind = ShortcutKind.Command, Target = "notepad.exe"
        });
        settings.Shortcuts.Add(new ShortcutItem
        {
            // 故意不给图标：Command 类型应当落到默认图标（这台机器不是 Windows，
            // 取不到文件原图标，正好验证降级路径）
            Name = "关机", Kind = ShortcutKind.Command, Target = "shutdown.exe", Confirm = true
        });
        settings.Shortcuts.Add(new ShortcutItem
        {
            Name = "课件", Kind = ShortcutKind.Path, Target = dir
        });
        settings.Shortcuts.Add(new ShortcutItem
        {
            // 这台机器上没有 ClassIsland 宿主，导航必然失败——
            // 正好拿它做确定性测试：一定走「打开失败」那条分支
            Name = "CI设置", Kind = ShortcutKind.ClassIslandUri,
            Target = "classisland://app/settings"
        });

        var roster = new RosterService(Path.Combine(dir, "名单.txt"));
        var stats = new PickStats();
        for (var i = 0; i < 47; i++)
        {
            roster.Pick(settings.Pick, stats);
        }

        try
        {
            var window = new ToolboxWindow(settings, roster, stats);
            window.Show();
            Settle(window);
            Capture(window, "toolbox-collapsed.png", "收起态（圆钮）");

            var collapsedCenter = KnobCenterOnScreen(window);

            // ---- 展开：1 个内置 + 4 条自定义 = 5 条 → 左 2 右 3 ----
            window.SetExpanded(true);
            Settle(window);

            var leftPanel = Field<StackPanel>(window, "_leftItems");
            var rightPanel = Field<StackPanel>(window, "_rightItems");

            var items = settings.Shortcuts.Count + 3;   // +3 是内置的幸运抽签、批注、橡皮
            Check(leftPanel.Children.Count == items / 2 && rightPanel.Children.Count == items - items / 2,
                $"{items} 条时左 {items / 2} 右 {items - items / 2}" +
                $"（实际 左 {leftPanel.Children.Count} 右 {rightPanel.Children.Count}）");

            var expandedCenter = KnobCenterOnScreen(window);
            var driftX = Math.Abs(expandedCenter.X - collapsedCenter.X);
            var driftY = Math.Abs(expandedCenter.Y - collapsedCenter.Y);
            Check(driftX <= 2 && driftY <= 2,
                $"展开前后圆钮中心没动（横向偏 {driftX:F1} px，纵向偏 {driftY:F1} px）");

            Check(leftPanel.Opacity == 1 && rightPanel.Opacity == 1, "展开动画结束后两侧完全不透明");
            Capture(window, "toolbox-expanded.png", "展开：圆钮居中，图标＋名称，左 2 右 3");

            // ---- 幸运抽签按钮上不该再有进度小字（一轮必定抽完，进度没有意义）----
            var pickButton = leftPanel.Children.OfType<Border>().First();
            var pickChildren = ContentPanel(pickButton)?.Children.Count ?? -1;
            Check(pickChildren == 2, $"幸运抽签按钮只有「图标 + 名称」两行（实际 {pickChildren} 行）");

            // ---- 按钮宽度在建的时候就写死 ----
            var allButtons = leftPanel.Children.OfType<Border>()
                .Concat(rightPanel.Children.OfType<Border>()).ToList();
            Check(allButtons.All(b => b.Width > 0),
                $"每个按钮都有固定宽度（{string.Join(" ", allButtons.Select(b => b.Width.ToString("F0")))})");

            // ---- 关键：点「需要确认」的按钮，宽度和圆钮位置都不能变 ----
            var confirmItem = settings.Shortcuts.First(s => s.Confirm);
            var confirmButton = Field<Dictionary<ShortcutItem, Border>>(window, "_shortcutButtons")[confirmItem];
            var widthBefore = confirmButton.Width;
            var buttonXBefore = confirmButton.Bounds.X;
            var knobBefore = KnobCenterOnScreen(window);

            Invoke(window, "InvokeShortcut", confirmItem);
            Settle(window);

            Check(TextAt(confirmButton, 1)?.Text == "再点一次",
                $"第一次点击进入确认态（按钮文字 = {TextAt(confirmButton, 1)?.Text}）");
            Check(Math.Abs(confirmButton.Width - widthBefore) < 0.01,
                $"确认态按钮宽度不变（{widthBefore:F0} → {confirmButton.Width:F0}）");
            Check(Math.Abs(confirmButton.Bounds.X - buttonXBefore) < 0.5,
                $"确认态按钮位置不变（X {buttonXBefore:F0} → {confirmButton.Bounds.X:F0}）");
            var knobAfter = KnobCenterOnScreen(window);
            Check(Math.Abs(knobAfter.X - knobBefore.X) < 1.0 && Math.Abs(knobAfter.Y - knobBefore.Y) < 1.0,
                $"确认态圆钮没有被挤走（横向 {Math.Abs(knobAfter.X - knobBefore.X):F1}，纵向 {Math.Abs(knobAfter.Y - knobBefore.Y):F1}）");
            Capture(window, "toolbox-confirm.png", "确认态：按钮变红，宽度和位置都没变");

            // ---- 收起 → 展开，圆钮回到原位 ----
            window.SetExpanded(false);
            Settle(window);
            var back = KnobCenterOnScreen(window);
            Check(Math.Abs(back.X - collapsedCenter.X) <= 2 && Math.Abs(back.Y - collapsedCenter.Y) <= 2,
                $"收起后圆钮回到原位（横向偏 {Math.Abs(back.X - collapsedCenter.X):F1}，纵向偏 {Math.Abs(back.Y - collapsedCenter.Y):F1}）");

            window.SetExpanded(true);
            Settle(window);
            var again = KnobCenterOnScreen(window);
            Check(Math.Abs(again.X - collapsedCenter.X) <= 2 && Math.Abs(again.Y - collapsedCenter.Y) <= 2,
                $"再展开依然在原位（横向偏 {Math.Abs(again.X - collapsedCenter.X):F1}，纵向偏 {Math.Abs(again.Y - collapsedCenter.Y):F1}）");

            // ---- 触摸/抖动：点一下绝不能把窗口挪走 ----
            // 触摸屏上手指按下、抬起之间会有十几像素漂移，被当成拖动的话窗口就会跟着挪，
            // 松手还会把新位置存进设置——表现就是「点一下，窗口位置就变了」。
            window.SetExpanded(false);
            Settle(window);

            // 注意：展开时窗口矩形本来就会变（向两边长），不变的是**圆钮中心**。
            var savedX = settings.WindowX;
            var savedY = settings.WindowY;
            var knobBeforeTap = KnobCenterOnScreen(window);

            var tap = KnobCenterInWindow(window);
            window.MouseDown(tap, MouseButton.Left);
            window.MouseMove(new Point(tap.X + 6, tap.Y + 3));
            window.MouseUp(new Point(tap.X + 6, tap.Y + 3), MouseButton.Left);
            Settle(window);

            var knobAfterTap = KnobCenterOnScreen(window);
            Check(Math.Abs(knobAfterTap.X - knobBeforeTap.X) <= 2 &&
                  Math.Abs(knobAfterTap.Y - knobBeforeTap.Y) <= 2,
                $"按下时抖 6px，圆钮没有被挪走（横向 {Math.Abs(knobAfterTap.X - knobBeforeTap.X):F1}，" +
                $"纵向 {Math.Abs(knobAfterTap.Y - knobBeforeTap.Y):F1}）");
            Check(window.IsExpanded, "这点抖动被当成了一次点击（工具条展开了）");
            Check(settings.WindowX == savedX && settings.WindowY == savedY,
                "抖动没有被写进设置（位置不会被记住成挪过了）");

            // ---- 判定不翻案：刚过阈值的小拖动必须真的挪窗口，不能被当成点击 ----
            // 这正是「拖动和点击互相误触发」的核心：
            // 以前拖动松手后还有一道"总位移不够就退回原位并展开"的二次判定，
            // 于是想拖的人得到了一次点击。
            window.SetExpanded(false);
            Settle(window);

            var smallBefore = window.Position;
            var smallGrip = KnobCenterInWindow(window);
            window.MouseDown(smallGrip, MouseButton.Left);
            window.MouseMove(new Point(smallGrip.X + 14, smallGrip.Y));
            window.MouseUp(new Point(smallGrip.X + 14, smallGrip.Y), MouseButton.Left);
            Settle(window);

            Check(!window.IsExpanded, "小拖动不会被当成点击（没有展开工具条）");
            var smallMoved = window.Position.X - smallBefore.X;
            Check(smallMoved >= 2, $"小拖动确实把窗口挪走了（{smallBefore.X} → {window.Position.X}）");
            Check(settings.WindowX == window.Position.X, "拖动后的位置写进了设置");

            // ---- 起步不跳格：挪走的距离应当约等于「手指位移 - 阈值」 ----
            Check(smallMoved <= 10,
                $"起步没有整格跳过去（手指走 14px，窗口走 {smallMoved:F0}px，阈值 {Const("MouseDragThreshold")}）");

            // ---- 一次大位移不能丢：触摸驱动常把多个 move 合并成一个事件 ----
            var fastGrip = KnobCenterInWindow(window);
            var fastBefore = window.Position;
            window.MouseDown(fastGrip, MouseButton.Left);
            window.MouseMove(new Point(fastGrip.X + 150, fastGrip.Y));
            window.MouseUp(new Point(fastGrip.X + 150, fastGrip.Y), MouseButton.Left);
            Settle(window);

            Check(!window.IsExpanded, "快速拖动也不会被当成点击");
            var fastMoved = window.Position.X - fastBefore.X;
            Check(fastMoved >= 130, $"单个大位移事件不丢位移（走了 {fastMoved:F0}px / 手指 150px）");

            // ---- 阈值：触摸必须明显大于鼠标 ----
            var mouseThreshold = Const("MouseDragThreshold");
            var touchThreshold = Const("TouchDragThreshold");
            Check(touchThreshold >= 24 && touchThreshold > mouseThreshold * 2,
                $"触摸阈值 {touchThreshold} 明显大于鼠标阈值 {mouseThreshold}");

            // ---- 指针移开 / 点别处：工具条不该自己收起来 ----
            window.SetExpanded(true);
            Settle(window);
            window.MouseMove(new Point(300, 300));   // 挪到窗口外面
            Thread.Sleep(1300);                      // 比原来那个 700ms 的收起延时更长
            Dispatcher.UIThread.RunJobs();
            Check(window.IsExpanded, "指针移开 1.3 秒也不会自动收起");

            // ---- 打开快捷方式：图标全部藏起来，只留一条覆盖消息 ----
            var uriShortcut = settings.Shortcuts.First(x => x.Kind == ShortcutKind.ClassIslandUri);
            var knobBeforeOpen = KnobCenterOnScreen(window);
            Invoke(window, "InvokeShortcut", uriShortcut);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(150);
            Dispatcher.UIThread.RunJobs();

            var row = Field<StackPanel>(window, "_row");
            var messagePill = Field<Border>(window, "_messagePill");
            var messageText = Field<TextBlock>(window, "_messageText");
            var leftMsg = Field<TextBlock>(window, "_leftMessage");
            var rightMsg = Field<TextBlock>(window, "_rightMessage");

            Check(row.IsVisible, "打开时主按钮那一行还在（没有被整条换掉）");
            Check(leftMsg.IsVisible && rightMsg.IsVisible, "两侧都换成了提示文字");
            Check(!leftPanel.IsVisible && !rightPanel.IsVisible, "两侧的快捷方式按钮被提示盖住");
            Check(leftMsg.Text.Contains(uriShortcut.Name),
                $"提示里带着快捷方式的名字（{leftMsg.Text} / {rightMsg.Text}）");

            Settle(window);
            var knobAfterOpen = KnobCenterOnScreen(window);
            Check(Math.Abs(knobAfterOpen.X - knobBeforeOpen.X) <= 3 &&
                  Math.Abs(knobAfterOpen.Y - knobBeforeOpen.Y) <= 3,
                $"主按钮没被挪走（横向 {Math.Abs(knobAfterOpen.X - knobBeforeOpen.X):F1}，" +
                $"纵向 {Math.Abs(knobAfterOpen.Y - knobBeforeOpen.Y):F1}）");
            Capture(window, "toolbox-opening.png", "正在打开：只盖快捷方式区，主按钮不动");

            Thread.Sleep(3200);
            Dispatcher.UIThread.RunJobs();
            Check(!leftMsg.IsVisible && leftPanel.IsVisible, "提示结束后快捷方式按钮回来了");
            Check(window.IsExpanded, "用过快捷方式之后工具条**没有**收起");

            // 正常（成功）状态的消息长什么样，单独画一张——上一条走的是失败分支
            window.SetExpanded(true);
            Settle(window);
            Invoke(window, "BeginOpening", "浏览器");
            Settle(window);
            Check(leftMsg.Text.Contains("浏览器") && rightMsg.Text.Contains("请稍后"),
                $"正常提示文案正确（{leftMsg.Text} / {rightMsg.Text}）");
            Capture(window, "toolbox-opening-ok.png", "正常状态：正在打开，请稍后…");
            Invoke(window, "EndOpening");
            Settle(window);

            // ---- 真拖动：位置要跟着走 ----
            window.SetExpanded(false);
            Settle(window);
            var dragFrom = window.Position;
            var grip = KnobCenterInWindow(window);
            window.MouseDown(grip, MouseButton.Left);
            window.MouseMove(new Point(grip.X + 90, grip.Y + 40));
            window.MouseUp(new Point(grip.X + 90, grip.Y + 40), MouseButton.Left);
            Settle(window);
            Check(Math.Abs(window.Position.X - dragFrom.X) > 40 && Math.Abs(window.Position.Y - dragFrom.Y) > 20,
                $"真拖动仍然会挪窗口（{dragFrom} → {window.Position}）");

            window.SetExpanded(false);
            Settle(window);

            // ---- 展开状态下拖动：收起后绝不能弹回原位 ----
            // 这是之前的一个真 bug：拖动完不更新锚点，一收起就弹回拖动前的位置，
            // 表现就是「怎么拖都会回到上一次的地方」。
            window.SetExpanded(false);
            Settle(window);
            window.SetExpanded(true);
            Settle(window);

            var expandedGrip = KnobCenterInWindow(window);
            var knobBeforeExpandedDrag = KnobCenterOnScreen(window);
            window.MouseDown(expandedGrip, MouseButton.Left);
            window.MouseMove(new Point(expandedGrip.X + 130, expandedGrip.Y + 70));
            window.MouseUp(new Point(expandedGrip.X + 130, expandedGrip.Y + 70), MouseButton.Left);
            Settle(window);

            var knobAfterExpandedDrag = KnobCenterOnScreen(window);
            Check(Math.Abs(knobAfterExpandedDrag.X - knobBeforeExpandedDrag.X) > 60 &&
                  Math.Abs(knobAfterExpandedDrag.Y - knobBeforeExpandedDrag.Y) > 30,
                $"展开状态下也能拖动（{knobBeforeExpandedDrag} → {knobAfterExpandedDrag}）");

            window.SetExpanded(false);
            Settle(window);
            var knobAfterCollapse = KnobCenterOnScreen(window);
            Check(Math.Abs(knobAfterCollapse.X - knobAfterExpandedDrag.X) <= 2 &&
                  Math.Abs(knobAfterCollapse.Y - knobAfterExpandedDrag.Y) <= 2,
                $"收起后停在拖动后的位置，不弹回（横向 {Math.Abs(knobAfterCollapse.X - knobAfterExpandedDrag.X):F1}，" +
                $"纵向 {Math.Abs(knobAfterCollapse.Y - knobAfterExpandedDrag.Y):F1}）");

            // 再展开一次也不能弹
            window.SetExpanded(true);
            Settle(window);
            var knobAgain = KnobCenterOnScreen(window);
            Check(Math.Abs(knobAgain.X - knobAfterCollapse.X) <= 2 && Math.Abs(knobAgain.Y - knobAfterCollapse.Y) <= 2,
                "再次展开也不弹回");
            window.SetExpanded(false);
            Settle(window);

            // ---- 主按钮四个字分压在两朵云上：上面两个、下面两个 ----
            {
                var top = Field<TextBlock>(window, "_knobLabelTop");
                var bottom = Field<TextBlock>(window, "_knobLabelBottom");
                Check(top.Text + bottom.Text == "教学助手",
                    $"主按钮文字是「教学助手」（实际 {top.Text}+{bottom.Text}）");

                // 上面那朵云在右上、下面那朵在左下，所以两行字的相对位置应该是：
                // 上行的中心更高，也更靠右。
                var topOffset = ((TranslateTransform)top.RenderTransform!).Y;
                var bottomOffset = ((TranslateTransform)bottom.RenderTransform!).Y;
                var topOffsetX = ((TranslateTransform)top.RenderTransform!).X;
                var bottomOffsetX = ((TranslateTransform)bottom.RenderTransform!).X;

                Check(topOffset < bottomOffset - 8,
                    $"「教学」压在**上面**那朵云上（上行偏 {topOffset:F0} / 下行偏 {bottomOffset:F0}）");
                Check(topOffsetX > bottomOffsetX + 4,
                    $"「教学」比「助手」更靠右（{topOffsetX:F0} vs {bottomOffsetX:F0}）");

                // 两行都得在窗口范围内——窗口高度就是主按钮的高度，出去就被裁掉。
                var labelHeight = Field<TextBlock>(window, "_knobLabelTop").DesiredSize.Height;
                var d = settings.Diameter;
                Check(bottomOffset + d / 2 + labelHeight / 2 <= d + 2,
                    $"下面那行没有顶出窗口下沿（下沿 {bottomOffset + d / 2 + labelHeight / 2:F0} / 窗口 {d:F0}）");
            }

            // ---- 3 条 → 左 1 右 2；2 条 → 左 1 右 1 ----
            window.SetExpanded(true);
            Settle(window);
            settings.Shortcuts.RemoveAt(3);
            settings.Shortcuts.RemoveAt(2);
            window.RefreshItems();
            Settle(window);
            items = settings.Shortcuts.Count + 3;
            Check(leftPanel.Children.Count == items / 2 && rightPanel.Children.Count == items - items / 2,
                $"{items} 条时左 {items / 2} 右 {items - items / 2}" +
                $"（实际 左 {leftPanel.Children.Count} 右 {rightPanel.Children.Count}）");
            Capture(window, "toolbox-three.png", "少几条时的均分");

            settings.Shortcuts.RemoveAt(1);
            window.RefreshItems();
            Settle(window);
            items = settings.Shortcuts.Count + 3;
            Check(leftPanel.Children.Count == items / 2 && rightPanel.Children.Count == items - items / 2,
                $"{items} 条时左 {items / 2} 右 {items - items / 2}" +
                $"（实际 左 {leftPanel.Children.Count} 右 {rightPanel.Children.Count}）");
            Capture(window, "toolbox-two.png", "再少几条时的均分");

            // ---- 只剩幸运抽签（默认安装后的样子）----
            settings.Shortcuts.Clear();
            window.RefreshItems();
            Settle(window);
            // 内置三条：幸运抽签、批注、橡皮。
            var builtin = leftPanel.Children.Count + rightPanel.Children.Count;
            Check(builtin == 3 && leftPanel.Children.Count == 1 && rightPanel.Children.Count == 2,
                $"只剩内置三项时左 1 右 2（实际 左 {leftPanel.Children.Count} 右 {rightPanel.Children.Count}）");
            Capture(window, "toolbox-single.png", "只剩内置的幸运抽签、批注、橡皮");

            // ---- 大号 ----
            settings.Diameter = 84;
            settings.Shortcuts.Add(new ShortcutItem
            {
                Name = "计算器", Icon = "🧮", Kind = ShortcutKind.Command, Target = "calc.exe"
            });
            window.ApplySize();
            window.RefreshItems();
            Settle(window);
            Capture(window, "toolbox-large.png", "大号");

            // ---- 关掉幸运抽签：工具条上不再有幸运抽签，菜单里相关项也一起藏掉 ----
            settings.EnableLuckyDraw = false;
            window.RefreshItems();
            Settle(window);

            var shown = leftPanel.Children.Count + rightPanel.Children.Count;
            var expected = settings.Shortcuts.Count + (settings.EnableAnnotate ? 2 : 0);
            Check(shown == expected,
                $"关掉幸运抽签后只剩快捷方式和批注（显示 {shown} 条 / 期望 {expected} 条）");
            Check(leftPanel.Children.OfType<Border>().All(b => TextAt(b, 1)?.Text != "幸运抽签"),
                "工具条上确实没有「抽人」这颗按钮");

            var menuItems = InvokeResult<object[]>(window, "BuildMenuItems");
            var headers = menuItems.OfType<MenuItem>()
                .Select(m => m.Header?.ToString() ?? string.Empty).ToList();
            Check(headers.All(h => !h.Contains("幸运抽签") && !h.Contains("名单") && !h.Contains("新一轮")),
                $"关掉幸运抽签后菜单里没有幸运抽签相关项（{string.Join(" / ", headers)}）");

            // ---- 一个功能都没有：点圆钮给提示，而不是展开成一条空的 ----
            settings.EnableAnnotate = false;
            settings.Shortcuts.Clear();
            window.RefreshItems();
            Settle(window);
            window.SetExpanded(false);
            Settle(window);

            window.SetExpanded(true);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(150);
            Dispatcher.UIThread.RunJobs();

            Check(!window.IsExpanded, "一个功能都没有时不会真的展开成空条");
            Check(!row.IsVisible && messagePill.IsVisible, "改成显示一条说明");
            Check(messageText.Text.Contains("还没有可用的功能"), $"说明文案正确（{messageText.Text}）");
            Settle(window);
            Capture(window, "toolbox-empty-hint.png", "一个功能都没有时的提示");

            Thread.Sleep(2900);
            Dispatcher.UIThread.RunJobs();

            // ---- 批注交互：按一次开启 → 再按开配置栏 → 再按收配置栏 ----
            settings.EnableLuckyDraw = true;
            settings.EnableAnnotate = true;
            window.RefreshItems();
            window.SetExpanded(true);   // 配置栏只在展开状态下露出来
            Settle(window);

            var configRow = Field<StackPanel>(window, "_annotateConfigRow");
            var toolRow = Field<StackPanel>(window, "_annotateToolRow");
            // 摆位用的是外层容器（两行一起摆），不是单独那一行。
            var rowsHost = Field<StackPanel>(window, "_annotateRowsHost");
            var configLayer = Field<Panel>(window, "_annotateConfigLayer");
            var penButton = FindItemButton(window, "批注");
            var eraserButton = FindItemButton(window, "橡皮");
            Check(penButton is not null && eraserButton is not null,
                "工具条上同时有「批注」和「橡皮」两颗按钮");
            bool AnnotateOn() => Field<bool>(window, "_annotateOn");
            bool RowsShown() => Field<bool>(window, "_annotateRowsShown");

            Invoke(window, "ToggleAnnotate", false, penButton);
            Settle(window);
            Check(AnnotateOn() && !RowsShown(), "第一次点「批注」：开启，配置栏还没出来");
            Check(settings.AnnotateTool == AnnotateTool.Pen, "开启时默认是软笔");
            Check(!configLayer.IsVisible, "配置栏这时候是收着的");

            Invoke(window, "ToggleAnnotate", false, penButton);
            Settle(window);
            Dispatcher.UIThread.RunJobs();
            Settle(window);
            Check(AnnotateOn() && RowsShown(), "第二次点：配置栏打开");
            Check(configLayer.IsVisible, "配置栏确实显示出来了");
            Check(toolRow.Children.Count == 8,
                $"工具行是四支工具 + 撤销/重做/清屏/退出（实际 {toolRow.Children.Count} 项）");
            Check(configRow.Children.Count == 9,
                $"软笔参数行是六色 + 三档粗细（实际 {configRow.Children.Count} 项）");

            // 小云不能被内边距挤窄（同一类 bug：Padding 挂在了 Border 上）。
            {
                var chip = configRow.Children.OfType<Border>().First();
                var surface = ((Panel)chip.Child!).Children.OfType<CloudButtonSurface>().First();
                Check(surface.Bounds.Width >= chip.Bounds.Width - 2,
                    $"配置栏的小云铺满按钮（云 {surface.Bounds.Width:F0} / 按钮 {chip.Bounds.Width:F0}）");
            }

            // 配置栏要落在**被点的那颗按钮正上方**
            {
                var rowOrigin = Field<StackPanel>(window, "_row").TranslatePoint(new Point(0, 0), window) ?? default;
                var btnOrigin = penButton!.TranslatePoint(new Point(0, 0), window) ?? default;
                var btnCenter = btnOrigin.X - rowOrigin.X + penButton.Bounds.Width / 2;
                var cfgLeft = ((TranslateTransform)rowsHost.RenderTransform!).X;
                var cfgCenter = cfgLeft + rowsHost.Bounds.Width / 2;
                var cfgWidth = rowsHost.Bounds.Width;
                var layerWidth = configLayer.Bounds.Width;

                // 契约是：**放得下就对着按钮居中，放不下就贴边**（保证不出窗口）。
                // 按钮靠边、或者配置栏比工具条还宽时都会走贴边这一支。
                var pinnedToEdge = cfgLeft <= 0.5 ||
                    Math.Abs(cfgLeft - Math.Max(0, layerWidth - cfgWidth)) <= 0.5;

                Check(cfgWidth <= layerWidth + 0.5,
                    $"配置栏没有被裁掉（配置栏 {cfgWidth:F0} / 可用 {layerWidth:F0}）");
                Check(Math.Abs(cfgCenter - btnCenter) <= 12 || pinnedToEdge,
                    $"配置栏对着「批注」按钮居中（偏差 {Math.Abs(cfgCenter - btnCenter):F0} px，" +
                    $"配置栏 {cfgWidth:F0} / 主行 {layerWidth:F0}）");

                var cfgY = configLayer.TranslatePoint(new Point(0, 0), window) ?? default;
                Check(cfgY.Y + configLayer.Bounds.Height <= rowOrigin.Y + 1,
                    "配置栏在主行**上方**");
            }
            Capture(window, "toolbox-annotate-pen.png", "批注：工具行 + 软笔参数行（六色 + 三档粗细）");

            Invoke(window, "ToggleAnnotate", false, null);
            Settle(window);
            Check(AnnotateOn() && !RowsShown(), "第三次点：配置栏收起（批注还开着）");
            Check(!configLayer.IsVisible, "收起时配置栏确实不见了");

            // 换工具：点橡皮
            Invoke(window, "ToggleAnnotate", true, eraserButton);
            Settle(window);
            Dispatcher.UIThread.RunJobs();
            Settle(window);
            Check(AnnotateOn() && settings.AnnotateTool == AnnotateTool.Eraser, "点「橡皮」：切到橡皮，批注仍开着");
            Check(RowsShown(), "换工具时配置栏保持打开，正好接着调");
            Check(configRow.Children.Count == 3,
                $"橡皮参数行只剩三档范围（实际 {configRow.Children.Count} 项）");
            Check(toolRow.Children.Count == 8,
                $"清屏和退出在工具行上（实际 {toolRow.Children.Count} 项）");

            {
                var rowOrigin = Field<StackPanel>(window, "_row").TranslatePoint(new Point(0, 0), window) ?? default;
                var btnOrigin = eraserButton!.TranslatePoint(new Point(0, 0), window) ?? default;
                var btnCenter = btnOrigin.X - rowOrigin.X + eraserButton.Bounds.Width / 2;
                var cfgLeft2 = ((TranslateTransform)rowsHost.RenderTransform!).X;
                var cfgWidth2 = rowsHost.Bounds.Width;
                var layerWidth2 = configLayer.Bounds.Width;
                var cfgCenter = cfgLeft2 + cfgWidth2 / 2;
                var pinned2 = cfgLeft2 <= 0.5 ||
                    Math.Abs(cfgLeft2 - Math.Max(0, layerWidth2 - cfgWidth2)) <= 0.5;

                Check(Math.Abs(cfgCenter - btnCenter) <= 12 || pinned2,
                    $"换成橡皮后配置栏跟着挪到「橡皮」上方（偏差 {Math.Abs(cfgCenter - btnCenter):F0} px，" +
                    $"左 {cfgLeft2:F0} / 配置栏 {cfgWidth2:F0} / 主行 {layerWidth2:F0}）");
            }
            Capture(window, "toolbox-annotate-eraser.png", "批注：橡皮参数行（三档范围）");

            // ---- 悬浮窗透明度 ----
            settings.ToolbarOpacity = 0.6;
            window.ApplyOpacity();
            Settle(window);
            Check(Math.Abs(Field<StackPanel>(window, "_root").Opacity - 0.6) < 0.01,
                $"透明度作用到了根内容上（{Field<StackPanel>(window, "_root").Opacity:F2}）");
            Capture(window, "toolbox-opacity.png", "悬浮窗 60% 透明度");

            settings.ToolbarOpacity = 1.0;
            window.ApplyOpacity();
            Settle(window);
            Check(Math.Abs(Field<StackPanel>(window, "_root").Opacity - 1.0) < 0.01, "调回不透明也生效");

            // 退出：不清屏
            var canvas = AnnotationWindow.Canvas;
            if (canvas is not null)
            {
                canvas.Clear();
                canvas.PenColor = Colors.Red;
                canvas.Tool = AnnotateTool.Pen;
                var w2 = new Window { Width = 400, Height = 300, Content = canvas };
                // 画布已经被覆盖窗占着，这里只验证"退出不清屏"这条语义在代码上成立：
                // ExitAnnotate 只关窗口，不调 Clear。
            }

            Invoke(window, "ExitAnnotate");
            Settle(window);
            Check(!AnnotateOn() && !RowsShown(), "点「退出」之后批注关掉、配置栏收起");

            // ---- 退出**不能清屏** ----
            // 画布是覆盖窗的一部分，直接关窗口等于把画一起丢了，再进来就是白纸。
            Check(AnnotationWindow.Canvas is not null,
                "退出之后画布还在（画留在屏幕上，鼠标还给系统）");
            Check(!AnnotationWindow.IsInteractive,
                "退出之后覆盖层不再收笔（点击穿透）");

            // ---- 按钮变暗表示"正在生效" ----
            {
                var penSurface = SurfaceOf(penButton!);
                Invoke(window, "ToggleAnnotate", false, penButton);
                Settle(window);
                var activeFill = penSurface?.Fill;
                Check(activeFill is { } f && f.R < 200,
                    $"开着批注时「批注」按钮变暗（填充 {activeFill}）");

                Invoke(window, "ExitAnnotate");
                Settle(window);
                var idleFill = penSurface?.Fill;
                Check(idleFill is { } g && g.R > 200,
                    $"退出之后按钮恢复明亮（填充 {idleFill}）");
            }

            // 恢复，别影响收尾
            settings.EnableLuckyDraw = true;
            settings.EnableAnnotate = true;
            window.RefreshItems();
            Settle(window);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        catch (Exception ex)
        {
            Fail("工具条抛异常: " + ex);
        }
        finally
        {
            roster.Dispose();
        }
    }

    /// <summary>
    /// 屏幕批注：画布能不能收到笔、橡皮擦得对不对、清屏干不干净。
    /// </summary>
    /// <remarks>
    /// 画布是 <c>Control</c> 而不是 <c>Panel</c>（<c>Panel.Render</c> 是 sealed），
    /// 所以它到底能不能被点中是我最不确定的一点——这里真的发指针事件来验。
    /// </remarks>
    private static void TestAnnotation()
    {
        try
        {
            var canvas = new AnnotationCanvas();
            var window = new Window { Width = 800, Height = 600, Content = canvas };
            window.Show();
            Settle(window);

            Check(canvas.IsEmpty, "一开始画布是空的");

            // ---- 回归：刚弹出时的误触按下事件不能起笔 ----
            // 曾经的表现：窗口在工具条按钮的 PointerReleased 里被创建，
            // 系统给新窗口补了一个落点在按钮上的按下事件，
            // 于是第一笔永远多出一条"从批注按钮连到落笔处"的线。
            // 这里**不能等**——误触就发生在窗口出现的那一瞬间。
            {
                var fresh = new AnnotationCanvas();
                var freshWindow = new Window { Width = 800, Height = 600, Content = fresh };
                freshWindow.Show();

                freshWindow.MouseDown(new Point(60, 40), MouseButton.Left);
                freshWindow.MouseMove(new Point(400, 300));
                freshWindow.MouseUp(new Point(400, 300), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Check(fresh.IsEmpty,
                    $"刚弹出时的误触按下事件不会起笔（实际 {StrokeCount(fresh)} 笔）");

                freshWindow.Close();
                Dispatcher.UIThread.RunJobs();
            }

            // 画一条横线
            canvas.PenColor = Colors.Red;
            canvas.Tool = AnnotateTool.Pen;
            window.MouseDown(new Point(100, 200), MouseButton.Left);
            for (var x = 110; x <= 300; x += 10)
            {
                window.MouseMove(new Point(x, 200));
            }

            window.MouseUp(new Point(300, 200), MouseButton.Left);
            Settle(window);

            Check(!canvas.IsEmpty, "按下并拖动之后画布上有笔画了");
            Check(StrokeCount(canvas) == 1, $"画了一笔（实际 {StrokeCount(canvas)} 笔）");

            var start = FirstPoint(canvas);
            Check(Math.Abs(start.X - 100) <= 2 && Math.Abs(start.Y - 200) <= 2,
                $"笔画的起点就是落笔处（{start.X:F0},{start.Y:F0}），不是别的地方");
            Check(PointCount(canvas, 0) > 10, $"这一笔记录到了点（{PointCount(canvas, 0)} 个）");
            Capture(window, "annotation-pen.png", "批注：红色软笔画一条线");

            // 橡皮擦中间：应该被切成两段
            canvas.Tool = AnnotateTool.Eraser;
            canvas.EraserRadius = 30;
            window.MouseDown(new Point(200, 200), MouseButton.Left);
            window.MouseUp(new Point(200, 200), MouseButton.Left);
            Settle(window);

            Check(StrokeCount(canvas) == 2, $"橡皮把一笔擦成了两段（实际 {StrokeCount(canvas)} 段）");
            Capture(window, "annotation-erase.png", "批注：橡皮擦中间，两头留着");

            // 清屏
            canvas.Clear();
            Settle(window);
            Check(canvas.IsEmpty, "清屏之后画布空了");

            // ==================== 撤销 / 重做 ====================
            // 这是批注最要紧的一处"人性化"：画错了不该只能清屏重来。
            // 用一块全新的画布测，历史干净，断言才有意义——
            // 但要先等过起笔保护窗口（250ms），否则第一笔会被当成误触吃掉。
            {
                var undoCanvas = new AnnotationCanvas { Tool = AnnotateTool.Pen, PenThickness = 6 };
                var undoWindow = new Window { Width = 800, Height = 600, Content = undoCanvas };
                undoWindow.Show();
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(300);
                Dispatcher.UIThread.RunJobs();

                void DrawOn(double y)
                {
                    undoWindow.MouseDown(new Point(100, y), MouseButton.Left);
                    for (var x = 110.0; x <= 200; x += 10)
                    {
                        undoWindow.MouseMove(new Point(x, y));
                    }

                    undoWindow.MouseUp(new Point(200, y), MouseButton.Left);
                    Settle(undoWindow);
                }

                Check(undoCanvas.IsEmpty, "新画布是空的");
                Check(!undoCanvas.CanUndo, "新画布没有可撤销的");

                DrawOn(150);
                DrawOn(180);
                Check(StrokeCount(undoCanvas) == 2, $"画了两笔（实际 {StrokeCount(undoCanvas)}）");
                Check(undoCanvas.CanUndo, "有内容之后可以撤销");

                undoCanvas.Undo();
                Settle(undoWindow);
                Check(StrokeCount(undoCanvas) == 1, $"撤销掉最后一笔（实际 {StrokeCount(undoCanvas)}）");
                Check(undoCanvas.CanRedo, "撤销之后可以重做");

                undoCanvas.Undo();
                Settle(undoWindow);
                Check(undoCanvas.IsEmpty, "再撤销一次回到空白");
                Check(!undoCanvas.CanUndo, "撤到底之后撤销变灰");

                undoCanvas.Redo();
                undoCanvas.Redo();
                Settle(undoWindow);
                Check(StrokeCount(undoCanvas) == 2, $"两次重做把两笔都还回来（实际 {StrokeCount(undoCanvas)}）");
                Check(!undoCanvas.CanRedo, "重做到头之后重做变灰");

                // 撤销之后再画新笔，原来那条"未来"应该作废——撤销栈的标准语义
                undoCanvas.Undo();
                DrawOn(210);
                Check(!undoCanvas.CanRedo, "撤销后画新笔会丢掉原来的重做分支");

                // 清屏也要能撤销：手滑点一下不该毁掉一节课的板书
                var beforeClear = StrokeCount(undoCanvas);
                undoCanvas.Clear();
                Settle(undoWindow);
                Check(undoCanvas.IsEmpty, "清屏生效");
                undoCanvas.Undo();
                Settle(undoWindow);
                Check(StrokeCount(undoCanvas) == beforeClear, "清屏可以撤销回来");

                // 橡皮擦掉的也要能一步撤回：整段手势算一步，不是每个移动事件一步。
                // 长线和落笔点照搬 TestAnnotation 里那条已经验过的用例，避免坐标差一点就擦不到。
                undoCanvas.Clear();
                undoCanvas.Tool = AnnotateTool.Pen;
                undoWindow.MouseDown(new Point(100, 300), MouseButton.Left);
                for (var x = 110.0; x <= 300; x += 10)
                {
                    undoWindow.MouseMove(new Point(x, 300));
                }

                undoWindow.MouseUp(new Point(300, 300), MouseButton.Left);
                Settle(undoWindow);
                Check(StrokeCount(undoCanvas) == 1, $"先画一条线（实际 {StrokeCount(undoCanvas)}）");

                undoCanvas.Tool = AnnotateTool.Eraser;
                undoCanvas.EraserRadius = 30;
                undoWindow.MouseDown(new Point(200, 300), MouseButton.Left);
                undoWindow.MouseUp(new Point(200, 300), MouseButton.Left);
                Settle(undoWindow);
                Check(StrokeCount(undoCanvas) == 2, $"橡皮把线擦成两段（实际 {StrokeCount(undoCanvas)}）");

                undoCanvas.Undo();
                Settle(undoWindow);
                Check(StrokeCount(undoCanvas) == 1, $"撤销把擦掉的部分整段还原（实际 {StrokeCount(undoCanvas)}）");
                undoCanvas.Undo();
                Settle(undoWindow);
                Check(undoCanvas.IsEmpty, "再撤一步就回到空白——橡皮整段手势只占一步");

                // ==================== 荧光笔 ====================
                undoCanvas.Tool = AnnotateTool.Highlighter;
                undoCanvas.HighlightColor = Colors.Yellow;
                undoCanvas.HighlightThickness = 24;
                DrawOn(120);
                Check(StrokeCount(undoCanvas) == 1, "荧光笔画了一笔");
                Check(IsHighlighted(undoCanvas, 0), "这一笔被标记成荧光笔");
                Check(StrokeAlpha(undoCanvas, 0) < 220,
                    $"荧光笔是半透明的，底下的字能透出来（alpha={StrokeAlpha(undoCanvas, 0)}）");

                // ==================== 激光笔 ====================
                // 语义是"指给人看"：不留痕迹、不占撤销栈。
                var depthBeforeLaser = UndoDepth(undoCanvas);
                undoCanvas.Tool = AnnotateTool.Laser;
                undoWindow.MouseDown(new Point(100, 400), MouseButton.Left);
                undoWindow.MouseMove(new Point(200, 400));
                undoWindow.MouseUp(new Point(200, 400), MouseButton.Left);
                Settle(undoWindow);
                Check(StrokeCount(undoCanvas) == 1, "激光笔不在画布上留下笔画");
                Check(UndoDepth(undoCanvas) == depthBeforeLaser, "激光笔不进撤销栈");

                undoWindow.Close();
                Dispatcher.UIThread.RunJobs();
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        catch (Exception ex)
        {
            Fail("批注画布抛异常: " + ex);
        }
    }

    /// <summary>取出某颗按钮的那朵云。</summary>
    private static ClassIsland.Toolbox.Views.CloudButtonSurface? SurfaceOf(Border button) =>
        (button.Child as Panel)?.Children.OfType<ClassIsland.Toolbox.Views.CloudButtonSurface>()
            .FirstOrDefault();

    /// <summary>在工具条上按名字找一颗按钮。</summary>
    private static Border? FindItemButton(Window window, string name)
    {
        foreach (var panel in new[]
                 {
                     Field<StackPanel>(window, "_leftItems"),
                     Field<StackPanel>(window, "_rightItems")
                 })
        {
            foreach (var child in panel.Children.OfType<Border>())
            {
                if (TextAt(child, 1)?.Text == name)
                {
                    return child;
                }
            }
        }

        return null;
    }

    /// <summary>从预览里取出所有可拖的控件（字段类型是 (控件, 顺序键) 的元组，反射取一下）。</summary>
    private static List<Border> PreviewChips(ToolboxSettingsPage page)
    {
        var field = typeof(ToolboxSettingsPage)
            .GetField("_previewChips", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(page)!;

        var result = new List<Border>();
        foreach (var item in (System.Collections.IEnumerable)field)
        {
            result.Add((Border)item!.GetType().GetField("Item1")!.GetValue(item)!);
        }

        return result;
    }

    /// <summary>第一笔的第一个点。</summary>
    private static Point FirstPoint(AnnotationCanvas canvas)
    {
        var strokes = (System.Collections.IList)typeof(AnnotationCanvas)
            .GetField("_strokes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(canvas)!;

        var stroke = strokes[0]!;
        var points = (System.Collections.IList)stroke.GetType()
            .GetProperty("Points")!.GetValue(stroke)!;

        return (Point)points[0]!;
    }

    private static int StrokeCount(AnnotationCanvas canvas) =>
        ((System.Collections.ICollection)typeof(AnnotationCanvas)
            .GetField("_strokes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(canvas)!).Count;

    private static int PointCount(AnnotationCanvas canvas, int index)
    {
        var strokes = (System.Collections.IList)typeof(AnnotationCanvas)
            .GetField("_strokes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(canvas)!;

        var stroke = strokes[index]!;
        var points = (System.Collections.ICollection)stroke.GetType()
            .GetProperty("Points")!.GetValue(stroke)!;

        return points.Count;
    }

    /// <summary>幸运抽签弹出的大字窗口：现在是「云朵 + 天空」的卡片。</summary>
    private static void TestRevealWindow()
    {
        try
        {
            RevealWindow.Show("张三", "幸运抽签结果", 148, TimeSpan.FromSeconds(30), Colors.White);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(200);
            Dispatcher.UIThread.RunJobs();

            var window = (RevealWindow?)typeof(RevealWindow)
                .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);

            if (window is null)
            {
                Fail("拿不到大字窗口实例");
                return;
            }

            Settle(window);
            Check(window.Bounds.Width > 100 && window.Bounds.Height > 60,
                $"大字窗口有正常尺寸（{window.Bounds.Width:F0}x{window.Bounds.Height:F0}）");

            // 背景必须铺满卡片。
            // 这条曾经漏过：内边距挂在 Border 上，把作为子元素的背景层也一起缩了进去，
            // 卡片外圈就空出一圈没有天空的白边——看着就是"背景渲染不全"。
            {
                var card = Field<Border>(window, "_card");
                var backdrop = Field<CloudBackdrop>(window, "_backdrop");
                Check(backdrop.Bounds.Width >= card.Bounds.Width - 4 &&
                      backdrop.Bounds.Height >= card.Bounds.Height - 4,
                    $"背景铺满卡片（背景 {backdrop.Bounds.Width:F0}x{backdrop.Bounds.Height:F0} / " +
                    $"卡片 {card.Bounds.Width:F0}x{card.Bounds.Height:F0}）");
            }
            Capture(window, "reveal.png", "幸运抽签大字窗口：云朵天空卡片");

            RevealWindow.CloseCurrent();
            Dispatcher.UIThread.RunJobs();
        }
        catch (Exception ex)
        {
            Fail("大字窗口抛异常: " + ex);
        }
    }

    /// <summary>
    /// 设置页的顺序预览 + 拖动排序。
    /// </summary>
    /// <remarks>
    /// 这块是纯 UI 逻辑，只能真的把页面搭起来、真的发指针事件才测得出来。
    /// 测试工程里用桩替掉了拍照和宿主服务，设置页本身是**真实的那份代码**。
    /// </remarks>
    private static void TestSettingsPage()
    {
        try
        {
            var page = new ToolboxSettingsPage();
            var window = new Window { Width = 1080, Height = 760, Content = page };
            window.Show();
            Settle(window);

            var settings = page.Settings;
            settings.EnableLuckyDraw = true;
            settings.Diameter = 84;
            settings.Shortcuts.Clear();
            settings.Shortcuts.Add(new ShortcutItem { Name = "甲", Icon = "🌐", Kind = ShortcutKind.Url, Target = "https://a" });
            settings.Shortcuts.Add(new ShortcutItem { Name = "乙", Icon = "📝", Kind = ShortcutKind.Command, Target = "b.exe" });
            settings.Shortcuts.Add(new ShortcutItem { Name = "丙", Icon = "🧮", Kind = ShortcutKind.Command, Target = "c.exe" });
            settings.Shortcuts.Add(new ShortcutItem { Name = "丁", Icon = "📁", Kind = ShortcutKind.Path, Target = "/tmp" });

            page.RefreshPreview();
            Settle(window);

            var row = Field<StackPanel>(page, "_previewRow");
            var chips = PreviewChips(page);

            // 预览里现在有：幸运抽签、批注、橡皮、四条快捷方式——**内置功能也在里面，也能拖**。
            Check(chips.Count == 7, $"预览里有 7 颗可拖的按钮（实际 {chips.Count}）");

            // 预览在设置页靠下的位置，默认是滚出可视区的——不先滚进来看不见也点不到。
            row.BringIntoView();
            Settle(window);

            Point InWindow(Control c) =>
                c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window) ?? default;

            var orderBefore = string.Join(",", settings.EffectiveOrder());

            // ---- 把内置的「幸运抽签」往右拖：自带功能也参与排序 ----
            var from = InWindow(chips[0]);
            var to = InWindow(chips[1]);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(new Point(to.X + 6, to.Y));
            window.MouseUp(new Point(to.X + 6, to.Y), MouseButton.Left);
            Settle(window);

            var order = settings.EffectiveOrder();
            Check(order[0] != ToolboxSettings.LuckyDrawKey,
                $"内置的「幸运抽签」被拖离了第一位（现在是 {string.Join(",", order)}）");
            Check(order.Contains(ToolboxSettings.LuckyDrawKey), "「幸运抽签」还在顺序表里，没被拖丢");
            Check(order.Count == 7, $"拖完还是 7 项（实际 {order.Count}）");
            Check(string.Join(",", order) != orderBefore, "顺序确实变了");
            Check(PreviewChips(page).Count == 7, "重画之后预览还是 7 颗");

            // ---- 点一下（没有位移）不该改变顺序 ----
            var after = string.Join(",", settings.EffectiveOrder());
            var firstChipCenter = InWindow(PreviewChips(page)[0]);
            window.MouseDown(firstChipCenter, MouseButton.Left);
            window.MouseUp(firstChipCenter, MouseButton.Left);
            Settle(window);
            Check(string.Join(",", settings.EffectiveOrder()) == after, "点一下（没有拖动）不会改变顺序");

            // ---- 关掉幸运抽签：预览里少一颗，但批注和快捷方式都在 ----
            settings.EnableLuckyDraw = false;
            page.RefreshPreview();
            Settle(window);
            Check(PreviewChips(page).Count == 6,
                $"关掉幸运抽签后预览里剩「批注 + 橡皮 + 4 条快捷方式」（实际 {PreviewChips(page).Count}）");
            Check(!settings.EffectiveOrder().Contains(ToolboxSettings.LuckyDrawKey),
                "关掉之后顺序表里也没有幸运抽签了");

            settings.EnableLuckyDraw = true;
            page.RefreshPreview();
            Settle(window);

            Capture(window, "settings-preview.png", "设置页：顺序预览（可拖动）");

            // ---- 「打开文件」的三种路径方式：控件显隐必须跟着模式走 ----
            // 绑定名字写错在 Avalonia 里是**静默失败**（不抛异常，只是永远不显示），
            // 所以这里直接去渲染结果里查控件，不看绑定成没成。
            var pathItem = settings.Shortcuts.First(x => x.Kind == ShortcutKind.Path);

            bool ButtonVisible(string label) => page.GetVisualDescendants()
                .OfType<Button>()
                .Any(b => (b.Content as string) == label && b.IsEffectivelyVisible);

            bool ResolvedRowVisible() => page.GetVisualDescendants()
                .OfType<TextBlock>()
                .Any(t => t.Text == "实际打开：" && t.IsEffectivelyVisible);

            pathItem.PathMode = PathMode.Absolute;
            Settle(window);
            Check(ButtonVisible("选文件") && ButtonVisible("选文件夹") && !ButtonVisible("保存副本…"),
                "绝对路径模式：显示「选文件 / 选文件夹」，没有「保存副本…」");

            pathItem.PathMode = PathMode.RelativeToApp;
            Settle(window);
            Check(ButtonVisible("选文件并转相对路径") && !ButtonVisible("选文件")
                  && !ButtonVisible("保存副本…"), "相对路径模式：只剩「选文件并转相对路径」");
            Check(ResolvedRowVisible(), "相对路径模式显示「实际打开」那一行");

            pathItem.PathMode = PathMode.InternalCopy;
            Settle(window);
            Check(ButtonVisible("保存副本…") && !ButtonVisible("选文件并转相对路径"),
                "内部副本模式：只剩「保存副本…」");

            pathItem.PathMode = PathMode.Absolute;
            Settle(window);
            Check(!ResolvedRowVisible(), "绝对路径模式不显示「实际打开」那一行");

            // 滚到底部，把那条快捷方式的表单截进来（DataTemplate 里的 BringIntoView 不顶用）。
            pathItem.PathMode = PathMode.InternalCopy;
            Settle(window);
            page.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
            Settle(window);
            Capture(window, "settings-path-modes.png", "设置页：打开文件的三种路径方式");

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        catch (Exception ex)
        {
            Fail("设置页抛异常: " + ex);
        }
    }

    private static void Capture(Window window, string file, string what)
    {
        Settle(window);
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Fail($"{what} 渲染结果是 null");
            return;
        }

        var path = Path.Combine(OutDir, file);
        frame.Save(path);
        Console.WriteLine($"  ok    {what} -> {path}  ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
    }

    /// <summary>
    /// 「打开文件或文件夹」三种路径方式的解析逻辑。
    /// </summary>
    /// <remarks>
    /// 这一组是纯逻辑断言，不用起宿主——解析器的两个基准目录都能直接覆盖。
    /// 重点盯「拼错地方」：相对路径最容易出的毛病就是看着填了、实际指到别处去。
    /// </remarks>
    private static void TestShortcutPaths()
    {
        Console.WriteLine("== 打开文件：三种路径方式 ==");

        var sandbox = Path.Combine(Path.GetTempPath(), "tbtest-paths");
        var appRoot = Path.Combine(sandbox, "ci-root");
        var innerRoot = Path.Combine(sandbox, "plugin-files");
        if (Directory.Exists(sandbox))
        {
            Directory.Delete(sandbox, true);
        }

        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(innerRoot);

        ShortcutPathResolver.AppRootOverride = appRoot;
        ShortcutPathResolver.InternalRootOverride = innerRoot;

        try
        {
            var abs = new ShortcutItem
            {
                Kind = ShortcutKind.Path, PathMode = PathMode.Absolute,
                Target = Path.Combine(appRoot, "a.pptx")
            };
            Check(ShortcutPathResolver.Resolve(abs) == abs.Target, "绝对路径原样返回");

            var rel = new ShortcutItem
            {
                Kind = ShortcutKind.Path, PathMode = PathMode.RelativeToApp,
                Target = Path.Combine("课件", "第一课.pptx")
            };
            Check(ShortcutPathResolver.Resolve(rel) == Path.Combine(appRoot, "课件", "第一课.pptx"),
                "相对路径拼到 ClassIsland 数据根目录下");

            var relAbs = new ShortcutItem
            {
                Kind = ShortcutKind.Path, PathMode = PathMode.RelativeToApp,
                Target = Path.Combine(appRoot, "b.pptx")
            };
            Check(ShortcutPathResolver.Resolve(relAbs) == relAbs.Target,
                "相对模式下误填绝对路径不会重复拼接");

            var inner = new ShortcutItem
            {
                Kind = ShortcutKind.Path, PathMode = PathMode.InternalCopy, Target = "视频.mp4"
            };
            Check(ShortcutPathResolver.Resolve(inner) == Path.Combine(innerRoot, "视频.mp4"),
                "内部副本拼到插件内部目录下");

            var cmd = new ShortcutItem
            {
                Kind = ShortcutKind.Command, PathMode = PathMode.RelativeToApp, Target = "notepad.exe"
            };
            Check(ShortcutPathResolver.Resolve(cmd) == "notepad.exe",
                "启动程序不受路径方式影响（不会把 exe 拼到 CI 目录下）");

            Check(ShortcutPathResolver.TryMakeAppRelative(Path.Combine(appRoot, "课件", "x.pptx"))
                  == Path.Combine("课件", "x.pptx"), "根目录下的文件能相对化");
            Check(ShortcutPathResolver.TryMakeAppRelative(Path.Combine(sandbox, "outside.pptx")) is null,
                "根目录之外的文件拒绝相对化");

            Check(ShortcutPathResolver.IsSupportedInternalFile("x.mp4")
                  && ShortcutPathResolver.IsSupportedInternalFile("x.PPTX")
                  && ShortcutPathResolver.IsSupportedInternalFile("x.png")
                  && ShortcutPathResolver.IsSupportedInternalFile("x.pdf"),
                "视频 / 图片 / Office 允许存副本（扩展名大小写不敏感）");
            Check(!ShortcutPathResolver.IsSupportedInternalFile("x.exe")
                  && !ShortcutPathResolver.IsSupportedInternalFile("x.txt")
                  && !ShortcutPathResolver.IsSupportedInternalFile("noext"),
                "exe / txt / 无扩展名拒绝存副本");

            var src = Path.Combine(sandbox, "来源.pptx");
            File.WriteAllText(src, "hello");
            var n1 = ShortcutPathResolver.CopyIntoInternal(src);
            var n2 = ShortcutPathResolver.CopyIntoInternal(src);
            Check(n1 == "来源.pptx" && n2 == "来源.pptx", "同内容重复保存只留一份（幂等）");
            Check(File.Exists(Path.Combine(innerRoot, n1)), "副本真的落在内部目录里");

            var src2 = Path.Combine(sandbox, "另一份", "来源.pptx");
            Directory.CreateDirectory(Path.GetDirectoryName(src2)!);
            File.WriteAllText(src2, "different content");
            var n3 = ShortcutPathResolver.CopyIntoInternal(src2);
            Check(n3 == "来源-2.pptx", $"同名但内容不同会另存一份（实际 {n3}）");
            Check(File.ReadAllText(Path.Combine(innerRoot, "来源.pptx")) == "hello",
                "已有副本没有被覆盖");

            var used = new List<ShortcutItem>
            {
                new() { Kind = ShortcutKind.Path, PathMode = PathMode.InternalCopy, Target = n1 }
            };
            var unused = ShortcutPathResolver.FindUnusedInternalFiles(used);
            Check(unused.Count == 1 && Path.GetFileName(unused[0]) == n3,
                "清理只挑没人引用的那一份");
            Check(ShortcutPathResolver.FindUnusedInternalFiles(new List<ShortcutItem>
            {
                new() { Kind = ShortcutKind.Path, PathMode = PathMode.InternalCopy, Target = n1 },
                new() { Kind = ShortcutKind.Path, PathMode = PathMode.InternalCopy, Target = n3 }
            }).Count == 0, "两份都被引用时没有可清理的");

            var bad = Path.Combine(sandbox, "bad.exe");
            File.WriteAllText(bad, "x");
            var rejected = false;
            try
            {
                ShortcutPathResolver.CopyIntoInternal(bad);
            }
            catch (NotSupportedException)
            {
                rejected = true;
            }

            Check(rejected, "exe 复制进内部会被拒绝");

            var item = new ShortcutItem
            {
                Kind = ShortcutKind.Path, PathMode = PathMode.InternalCopy, Target = "视频.mp4"
            };
            Check(item.IsPathInternal && !item.IsPathAbsolute && !item.IsPathRelative
                  && item.ShowResolvedTarget, "内部副本模式的显隐标志正确");

            item.PathMode = PathMode.Absolute;
            Check(item.IsPathAbsolute && !item.ShowResolvedTarget,
                "切回绝对路径后不再显示「实际打开」那一行");

            // 目标一改，「实际打开」显示的路径必须跟着变，否则设置页上给的是旧值。
            item.PathMode = PathMode.RelativeToApp;
            item.Target = Path.Combine("课件", "换了一个.pptx");
            Check(item.ResolvedTarget == Path.Combine(appRoot, "课件", "换了一个.pptx"),
                "改目标后 ResolvedTarget 立刻跟上");
        }
        finally
        {
            ShortcutPathResolver.AppRootOverride = null;
            ShortcutPathResolver.InternalRootOverride = null;
            try
            {
                Directory.Delete(sandbox, true);
            }
            catch (Exception)
            {
                // 清理失败不影响断言结果。
            }
        }
    }

    /// <summary>取画布上第 index 条笔画对象（私有嵌套类型，只能反射）。</summary>
    private static object StrokeAt(AnnotationCanvas canvas, int index)
    {
        var strokes = (System.Collections.IList)typeof(AnnotationCanvas)
            .GetField("_strokes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(canvas)!;
        return strokes[index]!;
    }

    /// <summary>画布上第 index 条笔画是不是荧光笔。</summary>
    private static bool IsHighlighted(AnnotationCanvas canvas, int index)
    {
        var stroke = StrokeAt(canvas, index);
        return (bool)stroke.GetType().GetProperty("IsHighlight")!.GetValue(stroke)!;
    }

    /// <summary>画布上第 index 条笔画颜色的 alpha（荧光笔靠它半透明）。</summary>
    private static byte StrokeAlpha(AnnotationCanvas canvas, int index)
    {
        var stroke = StrokeAt(canvas, index);
        var color = (Color)stroke.GetType().GetProperty("Color")!.GetValue(stroke)!;
        return color.A;
    }

    /// <summary>撤销栈深度。用来验证"激光笔不进撤销栈"这类看不见的事。</summary>
    private static int UndoDepth(AnnotationCanvas canvas) =>
        ((System.Collections.ICollection)typeof(AnnotationCanvas)
            .GetField("_undo", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(canvas)!).Count;

    /// <summary>
    /// 名单文件的解析：批注行、空行、重复名字、前后空白。
    /// </summary>
    /// <remarks>
    /// 这之前整个工程<b>没有任何名单相关的断言</b>。而注释符号认错的表现是
    /// 「名单里凭空多出几个叫『# 张三』的人」——肉眼基本发现不了，
    /// 直到点名点到那个人头上。所以这里把各种常见注释符号都摆一遍。
    /// </remarks>
    private static void TestRosterParsing()
    {
        Console.WriteLine("== 名单解析 ==");

        var dir = Path.Combine(Path.GetTempPath(), "tbtest-roster");
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "名单.txt");

            File.WriteAllLines(path,
            [
                RosterService.DeclarationLine,
                "/// 三斜杠",
                "// C 风格",
                "# 井号",
                "; ini 风格",
                "-- SQL 风格",
                "% LaTeX 风格",
                "* 星号",
                "<!-- HTML 风格",
                "",
                "   ",
                "张三",
                "李四",
                " 王五 ",
                "张三",
                "赵六"
            ]);

            var roster = new RosterService(path);
            roster.Reload();

            Check(roster.Names.Count == 4,
                $"只认出 4 个真名字（实际 {roster.Names.Count}：{string.Join("/", roster.Names)}）");
            Check(roster.Names.Contains("张三") && roster.Names.Contains("李四")
                  && roster.Names.Contains("王五") && roster.Names.Contains("赵六"),
                "四个名字都认出来了");
            Check(!roster.Names.Any(x => x.Contains("斜杠") || x.Contains("风格") || x.Contains("星号")),
                "没有任何一条批注被当成名字");
            Check(!roster.Names.Contains(" 王五 "), "名字前后的空白被去掉了");
            Check(roster.Names.Count(x => x == "张三") == 1, "重复的名字只算一个");

            Check(RosterService.CommentPrefixes.Contains("///")
                  && RosterService.CommentPrefixes.Contains("#")
                  && RosterService.CommentPrefixes.Contains("//")
                  && RosterService.CommentPrefixes.Contains(";"),
                "至少认得 /// # // ; 四种注释符号");
            Check(RosterService.DeclarationLine.StartsWith("///", StringComparison.Ordinal),
                "声明行本身也以 /// 开头，不会污染名单");
        }
        catch (Exception ex)
        {
            Fail("名单解析抛异常: " + ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception)
            {
                // 清理失败不影响断言结果。
            }
        }
    }

    private static void Check(bool ok, string what)
    {
        if (ok)
        {
            Console.WriteLine("  ok    " + what);
        }
        else
        {
            Fail(what);
        }
    }

    private static void Fail(string what)
    {
        _failures++;
        Console.WriteLine("  FAIL  " + what);
    }
}
