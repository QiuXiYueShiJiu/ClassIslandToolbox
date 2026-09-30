// 教学助手 v1.1.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System;

namespace ClassIsland.Toolbox.Views;

/// <summary>
/// 就地配置一个控件的小工具。
/// </summary>
/// <remarks>
/// Avalonia 的控件没法用对象初始化器挂事件，而这里的窗口界面又是纯代码搭的，
/// 没有这个 helper 就得为每个按钮先声明一个局部变量，读起来很碎。
/// </remarks>
internal static class ControlExtensions
{
    public static T Also<T>(this T control, Action<T> configure)
    {
        configure(control);
        return control;
    }
}
