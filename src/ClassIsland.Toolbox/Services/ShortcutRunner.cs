// 教学助手 v1.0.0：ClassIsland 置顶工具条插件
using System;
using System.Diagnostics;
using System.IO;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Toolbox.Models;

namespace ClassIsland.Toolbox.Services;

/// <summary>
/// 执行一条自定义快捷方式。
/// </summary>
/// <remarks>
/// 全部走 <see cref="ProcessStartInfo.UseShellExecute"/> = <c>true</c>，
/// 也就是交给系统的「打开方式」去处理：网址会进默认浏览器，文件夹会进资源管理器，
/// 文件和程序各按关联走。这样不用自己去分辨后缀，也不用管环境变量。
/// <para/>
/// 出错一律<b>吞掉异常、返回一句人话</b>，由调用方在工具条上显示——
/// 悬浮窗里弹异常对话框是最糟糕的体验，用户点一下整个界面就卡住了。
/// </remarks>
public static class ShortcutRunner
{
    /// <summary>执行快捷方式。</summary>
    /// <returns>成功返回 <c>null</c>，失败返回给用户看的一句话。</returns>
    public static string? Run(ShortcutItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Target))
        {
            return "这条快捷方式还没有填目标。";
        }

        try
        {
            switch (item.Kind)
            {
                case ShortcutKind.ClassIslandUri:
                {
                    var navigation = IAppHost.TryGetService<IUriNavigationService>();
                    if (navigation is null)
                    {
                        return "ClassIsland 的导航服务当前不可用。";
                    }

                    if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var uri))
                    {
                        return $"地址看不懂：{item.Target}";
                    }

                    navigation.Navigate(uri);
                    return null;
                }

                case ShortcutKind.Command:
                {
                    var startInfo = new ProcessStartInfo(item.Target)
                    {
                        UseShellExecute = true,
                        Arguments = item.Arguments ?? string.Empty
                    };

                    if (item.RunAsAdmin)
                    {
                        // runas 会弹 UAC。用户点了「否」会抛 Win32Exception，下面统一接住。
                        startInfo.Verb = "runas";
                    }

                    Process.Start(startInfo);
                    return null;
                }

                default:
                {
                    // 网址和文件/文件夹都交给系统的默认处理程序。
                    // 「打开文件或文件夹」的目标可能是相对路径或插件内部副本，
                    // 先解析成真正的绝对路径，再交给 ShellExecute。
                    var target = ShortcutPathResolver.Resolve(item);

                    // 解析完之后可能指到不存在的地方。直接丢给 ShellExecute
                    // 只会得到一句 Win32 的英文报错，这里先拦一道，
                    // 把「实际去哪儿找的」明明白白写出来。
                    if (item.Kind == ShortcutKind.Path &&
                        !File.Exists(target) && !Directory.Exists(target))
                    {
                        return item.PathMode == PathMode.InternalCopy
                            ? $"插件内部副本不在了：{target}"
                            : $"找不到要打开的东西：{target}";
                    }

                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
