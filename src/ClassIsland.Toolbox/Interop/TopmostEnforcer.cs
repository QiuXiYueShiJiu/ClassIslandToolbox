using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ClassIsland.Toolbox.Interop;

/// <summary>
/// 把窗口按在最顶层。
/// </summary>
/// <remarks>
/// 只设 <see cref="Window.Topmost"/> 是不够的：全屏应用、别的置顶窗口、
/// 甚至资源管理器重启，都会把窗口挤到下面去。所以这里额外做两件事：
/// <list type="number">
/// <item>加上 <c>WS_EX_TOOLWINDOW</c> 和 <c>WS_EX_NOACTIVATE</c>，
///       让它不进 Alt+Tab、不抢焦点——不抢焦点本身就减少了被系统降层的机会；</item>
/// <item>起一个秒级定时器反复调用 <c>SetWindowPos(HWND_TOPMOST)</c> 把它顶回去。</item>
/// </list>
/// 这套组合在 Windows 上足以稳定盖住任务栏和普通全屏窗口，而且全是文档化的公开 API，
/// 不需要改系统设置，也不用动别的进程。
/// </remarks>
public sealed class TopmostEnforcer : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    /// <summary>鼠标消息直接穿过去，交给下面的窗口。</summary>
    private const int WsExTransparent = 0x00000020;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly bool _preventActivation;
    private IntPtr _handle;

    /// <summary>上一次让位之后有没有恢复置顶。避免每个 tick 都重复调同一个 SetWindowPos。</summary>

    /// <summary>
    /// 开关「点击穿透」。
    /// </summary>
    /// <remarks>
    /// 用 <c>WS_EX_TRANSPARENT</c> 而不是只把 <c>IsHitTestVisible</c> 设成 false：
    /// 后者只影响 Avalonia 内部的命中测试，窗口本身仍然会把鼠标消息收下，
    /// 底下的程序还是点不到。批注退出之后要让画留着、又要把鼠标还给系统，必须走这个样式位。
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

        var style = GetWindowLong(handle, GwlExStyle);

        style = clickThrough
            ? style | WsExTransparent | WsExNoActivate | WsExToolWindow
            : style & ~WsExTransparent;

        SetWindowLong(handle, GwlExStyle, style);
    }

    /// <param name="window">要维持置顶的窗口。</param>
    /// <param name="interval">重申置顶的间隔。</param>
    /// <param name="preventActivation">
    /// 是否加 <c>WS_EX_NOACTIVATE</c>。悬浮钮要（不抢别人的焦点），
    /// 但菜单窗口**不能**加——它靠失焦来自动关闭，拿不到焦点就永远关不掉。
    /// </param>
    public TopmostEnforcer(Window window, TimeSpan? interval = null, bool preventActivation = true)
    {
        _window = window;
        _preventActivation = preventActivation;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Reassert();
    }

    /// <summary>
    /// 开始维持置顶。窗口必须已经显示出来，否则拿不到句柄。
    /// </summary>
    public void Attach()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 平台就只靠 Avalonia 自己的 Topmost。
            return;
        }

        _handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var style = GetWindowLong(_handle, GwlExStyle) | WsExToolWindow;
            if (_preventActivation)
            {
                style |= WsExNoActivate;
            }

            SetWindowLong(_handle, GwlExStyle, style);
        }
        catch (Exception)
        {
            // 拿不到扩展样式不影响后面的置顶重申。
        }

        Reassert();
        _timer.Start();
    }

    /// <summary>
    /// 立刻把窗口顶回最上层。位置和大小都不动，也不抢焦点。
    /// </summary>
    public void Reassert()
    {
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SetWindowPos(_handle, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
        catch (Exception)
        {
            // 单次失败不要紧，下一个 tick 会再来一次。
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
