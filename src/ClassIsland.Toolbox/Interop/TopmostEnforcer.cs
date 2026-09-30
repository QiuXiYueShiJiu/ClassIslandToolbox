// 教学助手 v1.1.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ClassIsland.Toolbox.Interop;

/// <summary>
/// 把窗口钉在最顶层。
/// </summary>
/// <remarks>
/// 光设 <see cref="Window.Topmost"/> 不够：全屏应用、别的置顶窗口、甚至资源管理器重启，
/// 都会把窗口挤下去。所以这里额外做两件事：
/// <list type="number">
/// <item>给窗口加上「工具窗口」和「不接受激活」两个扩展样式。前者让它不进 Alt+Tab，
///       后者让它不抢焦点——<b>不抢焦点本身就减少了被系统降层的机会</b>；</item>
/// <item>起一个秒级定时器，反复把它顶回去。单次失败不要紧，下一拍会再来一次。</item>
/// </list>
/// 整套只用文档化的公开 API，不改系统设置，也不碰别的进程。
/// <para/>
/// Win32 调用走 <see cref="LibraryImportAttribute"/>（.NET 7 起）：封送代码在<b>编译期</b>生成，
/// 不在运行时靠反射拼，裁剪和 AOT 下也不会被切掉。
/// </remarks>
public sealed partial class TopmostEnforcer : IDisposable
{
    // ---- Win32 常量 ----

    /// <summary>扩展样式在 <c>GetWindowLong</c> 里的索引。</summary>
    private const int IndexExStyle = -20;

    /// <summary>工具窗口：不出现在 Alt+Tab 列表里。</summary>
    private const int StyleToolWindow = 0x00000080;

    /// <summary>不接受激活：点到它也不会抢走当前窗口的焦点。</summary>
    private const int StyleNoActivate = 0x08000000;

    /// <summary>点击穿透：鼠标消息直接放给下面的窗口。<b>必须和 <see cref="StyleLayered"/> 同时使用。</b></summary>
    private const int StyleTransparent = 0x00000020;

    /// <summary>分层窗口。加了它，<see cref="StyleTransparent"/> 才会参与命中测试。</summary>
    private const int StyleLayered = 0x00080000;

    /// <summary><c>SetLayeredWindowAttributes</c> 的「按 alpha 值合成」标志。</summary>
    private const uint LwaAlpha = 0x00000002;

    private const uint FlagNoSize = 0x0001;
    private const uint FlagNoMove = 0x0002;
    private const uint FlagNoActivate = 0x0010;
    private const uint FlagShowWindow = 0x0040;

    /// <summary><c>SetWindowPos</c> 的「插到最顶层」句柄值。</summary>
    private static readonly IntPtr InsertAfterTopmost = new(-1);

    private readonly Window _window;
    private readonly DispatcherTimer _ticker;
    private readonly bool _refuseFocus;

    private IntPtr _handle;
    private bool _attached;

    /// <param name="window">要维持置顶的窗口。</param>
    /// <param name="interval">重申置顶的间隔。</param>
    /// <param name="preventActivation">
    /// 是否加「不接受激活」样式。悬浮钮要（不能抢走课件窗口的焦点），
    /// 但菜单窗口<b>不能</b>加——它靠失焦来自动关闭，拿不到焦点就永远关不掉。
    /// </param>
    public TopmostEnforcer(Window window, TimeSpan? interval = null, bool preventActivation = true)
    {
        _window = window;
        _refuseFocus = preventActivation;
        _ticker = new DispatcherTimer { Interval = interval ?? TimeSpan.FromSeconds(1) };
        _ticker.Tick += OnTick;
    }

    /// <summary>
    /// 开始维持置顶。
    /// </summary>
    /// <remarks>
    /// 窗口必须已经显示出来，否则拿不到句柄。非 Windows 上这里什么也不做，
    /// 只靠 Avalonia 自己的 <c>Topmost</c>。
    /// </remarks>
    public void Attach()
    {
        _attached = true;
        _handle = ResolveHandle();

        if (_handle != IntPtr.Zero)
        {
            // 「不进 Alt+Tab」是基本盘，跟 preventActivation 无关；
            // 「不接受激活」才是有开关的那个。
            var style = ReadExStyle() | StyleToolWindow;
            if (_refuseFocus)
            {
                style |= StyleNoActivate;
            }

            WriteExStyle(style);
        }

        Reassert();
        _ticker.Start();
    }

    /// <summary>
    /// 立刻把窗口顶回最上层。位置和大小都不动，也不抢焦点。
    /// </summary>
    public void Reassert()
    {
        if (!_attached || _handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            // 四个「不动」标志一起给：只改层序，别的一概不碰。
            SetWindowPos(_handle, InsertAfterTopmost, 0, 0, 0, 0,
                FlagNoMove | FlagNoSize | FlagNoActivate | FlagShowWindow);
        }
        catch (Exception)
        {
            // 单次失败不要紧，下一拍会再来一次。
        }
    }

    /// <summary>
    /// 开关「点击穿透」。
    /// </summary>
    /// <remarks>
    /// 必须走 <c>WS_EX_TRANSPARENT</c>，不能只把 <c>IsHitTestVisible</c> 设成 false：
    /// 后者只管 Avalonia 内部的命中测试，窗口照样把鼠标消息收下，底下的程序还是点不到。
    /// 批注退出之后要「画留着、鼠标还给系统」，只能靠这个样式位。
    /// <para/>
    /// 非 Windows 上退化成 <c>IsHitTestVisible</c>——至少键盘和 Avalonia 内部的行为是对的。
    /// </remarks>
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        window.IsHitTestVisible = !clickThrough;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, IndexExStyle);

        if (!clickThrough)
        {
            // 只摘掉穿透，另外两个标志是窗口的固有属性，下次进来还要用。
            SetWindowLong(handle, IndexExStyle, style & ~StyleTransparent);
            return;
        }

        // **必须连 WS_EX_LAYERED 一起加。**
        // WS_EX_TRANSPARENT 单独存在时只影响绘制顺序，**不参与命中测试**——
        // 窗口照样把鼠标消息全收下，底下什么都点不到，表现就是「退出批注后鼠标失灵」。
        // 只有分层窗口（LAYERED）上的 TRANSPARENT 才会让点击穿过去。
        style |= StyleTransparent | StyleNoActivate | StyleToolWindow | StyleLayered;
        SetWindowLong(handle, IndexExStyle, style);

        // 补了这一位之后窗口会被当成分层窗口，必须显式声明不透明度，
        // 否则默认整窗透明——那就从「挡住鼠标」变成「连画都看不见」了。
        SetLayeredWindowAttributes(handle, 0, 255, LwaAlpha);
    }

    public void Dispose()
    {
        _ticker.Stop();
        _ticker.Tick -= OnTick;
        _attached = false;
        _handle = IntPtr.Zero;
    }

    private void OnTick(object? sender, EventArgs e) => Reassert();

    /// <summary>拿窗口句柄。非 Windows 或窗口还没显示时返回 0。</summary>
    private IntPtr ResolveHandle() =>
        OperatingSystem.IsWindows()
            ? _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero
            : IntPtr.Zero;

    /// <summary>读扩展样式；读不到就当 0（后面的位运算照样能跑）。</summary>
    private int ReadExStyle()
    {
        try
        {
            return GetWindowLong(_handle, IndexExStyle);
        }
        catch (Exception)
        {
            // 读不到样式不影响置顶重申，那两个标志位少了也只是行为退化。
            return 0;
        }
    }

    private void WriteExStyle(int style)
    {
        try
        {
            SetWindowLong(_handle, IndexExStyle, style);
        }
        catch (Exception)
        {
            // 同上：写不上就算了。
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>声明分层窗口的不透明度。255 = 完全不透明，视觉上毫无变化。</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
}
