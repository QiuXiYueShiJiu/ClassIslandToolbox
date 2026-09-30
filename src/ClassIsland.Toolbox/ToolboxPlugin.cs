using System.Reflection;
using System.Runtime.Loader;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Toolbox.Services;
using ClassIsland.Toolbox.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.Toolbox;

/// <summary>
/// 置顶工具集插件入口。
/// </summary>
public class ToolboxPlugin : PluginBase
{
    private static readonly Assembly SelfAssembly = typeof(ToolboxPlugin).Assembly;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        EnsureAssemblyResolvable();

        // 幸运抽签抽到人之后把名字推到 ClassIsland 主界面那条提醒上。
        services.AddNotificationProvider<ToolboxNotificationProvider>();

        // 插件主体：工具条、设置、名单都归它管。
        var configFolder = PluginConfigFolder;

        // 「打开文件或文件夹」的第三种模式会把文件副本存进插件自己的目录。
        // 位置在这里钉一次，执行侧（ShortcutRunner）和设置页都从同一个地方取，
        // 免得两边各拼一次路径、拼出两个地方来。
        ShortcutPathResolver.EnsureInternalRoot(configFolder);

        services.AddHostedService(_ => new ToolboxHostService(configFolder));

        services.AddSettingsPage<ToolboxSettingsPage>();
    }

    /// <summary>
    /// 让 <c>avares://ClassIsland.Toolbox/...</c> 能被解析到。
    /// </summary>
    /// <remarks>
    /// Avalonia 的资源加载器按名字用 <see cref="Assembly.Load(AssemblyName)"/> 找程序集，
    /// 走的是默认 <see cref="AssemblyLoadContext"/>；插件却在独立的 PluginLoadContext 里，
    /// 默认上下文看不到它。设置页是 axaml，不挂这个回调就加载不出来。
    /// </remarks>
    private static void EnsureAssemblyResolvable()
    {
        var selfName = SelfAssembly.GetName().Name;
        AssemblyLoadContext.Default.Resolving += (_, requested) =>
            requested.Name == selfName ? SelfAssembly : null;
    }
}
