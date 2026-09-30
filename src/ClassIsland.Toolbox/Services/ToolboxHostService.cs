using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Toolbox.Models;
using ClassIsland.Toolbox.Views;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.Toolbox.Services;

/// <summary>
/// 插件主体：管住悬浮工具条、设置、名单，以及幸运抽签结果往哪儿显示。
/// </summary>
public class ToolboxHostService : IHostedService
{
    private readonly string _settingsPath;
    private readonly string _statsPath;
    private readonly string _configFolder;

    private ToolboxSettings _settings = new();
    private PickStats _stats = new();
    private RosterService? _roster;
    private ToolboxWindow? _window;

    /// <summary>上一条还在播的提醒。连着幸运抽签时先取消掉，免得在主界面上排队堆积。</summary>
    private NotificationRequest? _lastRequest;

    public ToolboxHostService(string pluginConfigFolder)
    {
        _configFolder = pluginConfigFolder;
        _settingsPath = Path.Combine(pluginConfigFolder, "settings.json");
        // 幸运抽签次数历史单独一份：它是公平幸运抽签的唯一依据，不和一堆开关混在一个文件里。
        _statsPath = Path.Combine(pluginConfigFolder, "幸运抽签统计.json");
    }

    #region 状态

    /// <summary>工具条的设置。设置页直接绑这上面。</summary>
    public ToolboxSettings Settings => _settings;

    /// <summary>幸运抽签设置（就是 <see cref="Settings"/> 里的那一份）。</summary>
    public PickSettings Pick => _settings.Pick;

    /// <summary>幸运抽签次数历史。</summary>
    public PickStats Stats => _stats;

    /// <summary>当前名单。</summary>
    public RosterService? Roster => _roster;

    /// <summary>名单文件路径。</summary>
    public string RosterPath => _roster?.RosterPath ?? string.Empty;

    /// <summary>设置文件路径。</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>幸运抽签统计文件路径。</summary>
    public string StatsPath => _statsPath;

    /// <summary>插件配置目录。拍照存原图、名单、设置都在里面。</summary>
    public string ConfigFolder => _configFolder;

    /// <summary>插件自己所在的目录。ONNX Runtime 和人脸模型都在这儿。</summary>
    public static string PluginDirectory =>
        Path.GetDirectoryName(typeof(ToolboxHostService).Assembly.Location) ?? string.Empty;

    // 用 getter 而不是只读字段：插件加载器（以及我们自己的测试）不一定会调用 StartAsync，
    // 每次现算一次不会有任何开销，却省掉了"还没启动就空引用"这一类问题。
    private string EffectiveRosterPath => Path.Combine(_configFolder, _settings.Pick.RosterFileName);

    #endregion

    #region 生命周期

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = ToolboxSettings.Load(_settingsPath);
        _stats = PickStats.Load(_statsPath);
        _roster = new RosterService(EffectiveRosterPath);

        // 宿主启动 IHostedService 的时候 Avalonia 主窗口不一定已经就绪，
        // 用 Background 优先级排队，等 UI 空下来再开窗口。
        Dispatcher.UIThread.Post(ShowToolboxWindow, DispatcherPriority.Background);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SaveAll();
        Dispatcher.UIThread.Post(() =>
        {
            RevealWindow.CloseCurrent();
            _window?.Close();
            _window = null;
        });
        // 批注层是全屏的，插件停了必须收掉，否则屏幕上会留下一张点不动的透明纸。
        AnnotationWindow.CloseOverlay();
        _roster?.Dispose();
        return Task.CompletedTask;
    }

    private void ShowToolboxWindow()
    {
        if (_window is not null || _roster is null)
        {
            return;
        }

        _window = new ToolboxWindow(_settings, _roster, _stats);
        _window.PickRequested += (_, _) => LuckyDraw();
        _window.StatsRequested += (_, _) => ShowStatsWindow();
        _window.SettingsChanged += (_, _) => SaveSettingsInternal();

        // 批注开关一变：先让工具条重新抢回置顶，否则它会被刚铺上来的批注层盖住、按钮点不到；
        // 顺便把状态落盘（笔色、橡皮档位）。
        _window.AnnotateChanged += (_, _) =>
        {
            _window.ReassertTopmost();
            SaveSettingsInternal();
        };
        _window.HideRequested += (_, _) =>
        {
            RevealWindow.CloseCurrent();
            AnnotationWindow.CloseOverlay();
            _window?.Hide();
            // 藏起来之后重装/重启 ClassIsland 才会回来。
        };
        _window.Show();
    }

    #endregion

    #region 持久化

    public void SaveSettings() => SaveSettingsInternal();

    public void SaveStats() => _stats.Save(_statsPath);

    public void SaveAll()
    {
        SaveSettingsInternal();
        SaveStats();
    }

    private void SaveSettingsInternal()
    {
        _window?.CapturePosition();
        _settings.Save(_settingsPath);
    }

    #endregion

    #region 幸运抽签

    /// <summary>点一次名：按设置决定抽名单还是拍照。</summary>
    public void LuckyDraw()
    {
        // 幸运抽签可能被整个关掉了（比如只要一个纯快捷方式工具条）。
        if (_roster is null || !_settings.EnableLuckyDraw)
        {
            return;
        }

        PickFromRoster();
    }

    /// <summary>按名单抽一个（均衡抽选）。</summary>
    private void PickFromRoster()
    {
        if (_roster is null)
        {
            return;
        }

        var name = _roster.Pick(_settings.Pick, _stats);
        // 抽中的人已经记进 _stats，这里顺手把设置和统计一起落盘。
        SaveAll();
        _window?.RefreshItems();

        if (name is null)
        {
            Reveal("名单是空的", isHint: true);
            return;
        }

        Reveal(name, isHint: false);

        if (_settings.ShowNotification)
        {
            SendNotification(name);
        }
    }

    private void Reveal(string text, bool isHint, string? note = null)
    {
        RevealWindow.Show(
            text,
            note,
            isHint ? _settings.RevealFontSize * 0.42 : _settings.RevealFontSize,
            TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds, 0.5, 30)),
            _window?.Accent ?? DefaultAccent);
    }

    private static readonly Avalonia.Media.Color DefaultAccent =
        Avalonia.Media.Color.FromRgb(0x5B, 0x8D, 0xEF);

    private void SendNotification(string name)
    {
        // 提供方是宿主用 AddHostedService 建的，这里按类型把那一份取回来。
        var provider = IAppHost.Host?.Services
            .GetServices<IHostedService>()
            .OfType<ToolboxNotificationProvider>()
            .FirstOrDefault();
        if (provider is null)
        {
            return;
        }

        // 连着幸运抽签时，上一条还没播完就来了下一条，主界面上会排队堆积。
        _lastRequest?.Cancel();

        var request = new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask("幸运抽签", hasRightIcon: false, factory: x =>
            {
                x.Duration = TimeSpan.FromSeconds(0.9);
                x.IsSpeechEnabled = false;
            }),
            OverlayContent = NotificationContent.CreateSimpleTextContent(name, factory: x =>
            {
                x.Duration = TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds + 2.0, 2.0, 30));
                x.IsSpeechEnabled = false;
            })
        };

        _lastRequest = request;
        provider.ShowNotification(request);
    }

    #endregion

    #region 其它窗口

    /// <summary>打开幸运抽签统计（饼状图）。</summary>
    public void ShowStatsWindow()
    {
        if (_roster is null)
        {
            return;
        }

        StatsWindow.Show(_settings.Pick, _roster, _stats, SaveAll);
    }

    /// <summary>按当前尺寸档位刷新工具条。</summary>
    public void ApplySizeToWindow() => _window?.ApplySize();

    /// <summary>透明度改了：作用到悬浮窗上。</summary>
    public void ApplyOpacityToWindow() => _window?.ApplyOpacity();

    /// <summary>快捷方式列表变了：重建工具条上的按钮。</summary>
    public void NotifyShortcutsChanged() => _window?.RefreshItems();

    /// <summary>幸运抽签开关被改了：刷新工具条；关掉的话顺便把摄像头也关掉。</summary>
    public void ApplyLuckyDrawEnabled() => _window?.RefreshItems();

    #endregion
}
