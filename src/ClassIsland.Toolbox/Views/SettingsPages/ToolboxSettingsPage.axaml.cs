// 教学助手 v1.0.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Toolbox.Models;
using ClassIsland.Toolbox.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.Toolbox.Views.SettingsPages;

/// <summary>
/// 置顶工具集的设置页。
/// </summary>
/// <remarks>
/// <b>数值类设置一律走本页的包装属性，不直接绑到 <see cref="ToolboxSettings"/> 上。</b>
/// 那些模型是普通 POCO（只有 <see cref="ShortcutItem"/> 实现了变更通知），
/// 直接绑的话拖完滑块旁边的数字不会跟着变。包装属性在写入之后顺手把关联的显示文字一起通知掉，
/// 还能立刻落盘。
/// </remarks>
[SettingsPageInfo("yueshijiu.toolbox.settings", "教学助手", SettingsPageCategory.External)]
public partial class ToolboxSettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly ToolboxHostService? _service;

    /// <summary>快捷方式编辑的落盘防抖：一边打字一边存太吵。</summary>
    private readonly DispatcherTimer _shortcutSaveTimer;

    /// <summary>预览那块「天」——和工具条一样只有下半截，按钮一半落在上面。</summary>
    private readonly CloudBackdrop _previewBoard = new()
    {
        ShowClouds = false,
        StrokeThickness = 1.4,
        VerticalAlignment = VerticalAlignment.Bottom
    };

    /// <summary>预览的容器：天在下面，按钮压在上面。</summary>
    private readonly Panel _previewStage = new();

    /// <summary>顺序预览那一行的容器（左一半 · 主按钮 · 右一半）。</summary>
    private readonly StackPanel _previewRow = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    /// <summary>预览里每一颗对应的控件和它的顺序键，按显示顺序排列（左组在前、右组在后）。</summary>
    private readonly List<(Border Chip, string Key)> _previewChips = [];

    /// <summary>正在被拖的是第几颗（显示顺序里的下标）；-1 = 没在拖。</summary>
    private int _draggingIndex = -1;

    /// <summary>按下之后有没有真的移动过。用来区分「点一下」和「拖」。</summary>
    private bool _previewDragged;

    private Point _previewPressPoint;

    /// <summary>拿不到插件服务时的兜底设置对象。</summary>
    /// <remarks>
    /// <b>必须是同一个实例。</b>以前这里写的是 <c>?? new ToolboxSettings()</c>——
    /// 那样每次读 <see cref="Settings"/> 都会新建一份，页面上改什么都存在一个马上被丢掉的临时对象里，
    /// 而且预览读到的和编辑的不是同一个东西，改了半天没反应。
    /// </remarks>
    private readonly ToolboxSettings _fallbackSettings = new();

    public ToolboxSettings Settings => _service?.Settings ?? _fallbackSettings;

    public ToolboxSettingsPage()
    {
        _service = IAppHost.Host?.Services
            .GetServices<IHostedService>()
            .OfType<ToolboxHostService>()
            .FirstOrDefault();

        // 快捷方式区要显示「内部副本存在哪儿」，进页面先把位置钉好。
        SyncInternalRoot();

        DataContext = this;
        InitializeComponent();

        // 快捷方式列表里任何一条被改了，都要存盘并让工具条重建按钮。
        Settings.Shortcuts.CollectionChanged += OnShortcutsCollectionChanged;
        foreach (var shortcut in Settings.Shortcuts)
        {
            shortcut.PropertyChanged += OnShortcutPropertyChanged;
        }

        _shortcutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _shortcutSaveTimer.Tick += (_, _) =>
        {
            _shortcutSaveTimer.Stop();
            _service?.SaveSettings();
            _service?.NotifyShortcutsChanged();
        };

        // 顺序预览：代码搭比写 XAML 省事——它的排布规则和工具条一模一样，
        // 复用同一套「左一半 / 右一半」的算法才不会两边走样。
        _previewStage.Children.Add(_previewBoard);
        _previewStage.Children.Add(_previewRow);
        this.FindControl<Panel>("PreviewHost")?.Children.Add(_previewStage);
        _previewRow.PointerPressed += OnPreviewPointerPressed;
        _previewRow.PointerMoved += OnPreviewPointerMoved;
        _previewRow.PointerReleased += OnPreviewPointerReleased;
        RefreshPreview();

    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    #region 悬浮钮

    /// <summary>工具条上是否显示「批注」。</summary>
    public bool EnableAnnotate
    {
        get => Settings.EnableAnnotate;
        set
        {
            Settings.EnableAnnotate = value;
            _service?.NotifyShortcutsChanged();
            RefreshPreview();
            Save(nameof(EnableAnnotate));
        }
    }

    /// <summary>
    /// 是否启用幸运抽签。
    /// </summary>
    /// <remarks>
    /// 改完要让工具条重建按钮（幸运抽签那一颗要出现/消失），关掉时还要顺手放掉摄像头。
    /// </remarks>
    public bool EnableLuckyDraw
    {
        get => Settings.EnableLuckyDraw;
        set
        {
            Settings.EnableLuckyDraw = value;
            _service?.ApplyLuckyDrawEnabled();
            RefreshPreview();
            Save(nameof(EnableLuckyDraw));
        }
    }

    /// <summary>
    /// 主按钮直径。滑动条直接绑这个。
    /// </summary>
    /// <remarks>
    /// 改完要同时做三件事：让工具条按新尺寸重排、刷新预览、落盘。
    /// 都是轻量操作，所以拖动过程中实时生效，不用松手才应用。
    /// </remarks>
    public double Diameter
    {
        get => Settings.Diameter;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), ToolboxSettings.MinDiameter, ToolboxSettings.MaxDiameter);
            if (Math.Abs(Settings.Diameter - clamped) < 0.01)
            {
                return;
            }

            Settings.Diameter = clamped;
            _service?.ApplySizeToWindow();
            RefreshPreview();
            Save(nameof(DiameterText), nameof(SizeSummary));
        }
    }

    public string DiameterText => $"{Settings.Diameter:F0} px";

    /// <summary>
    /// 悬浮窗不透明度，滑动条用百分比（30~100）。
    /// </summary>
    /// <remarks>
    /// 界面上用百分比而不是 0.3~1.0 的小数：老师看"70%"比看"0.7"直观得多。
    /// </remarks>
    public double OpacityPercent
    {
        get => Math.Round(Settings.ToolbarOpacity * 100);
        set
        {
            var clamped = Math.Clamp(Math.Round(value), ToolboxSettings.MinOpacity * 100,
                ToolboxSettings.MaxOpacity * 100);
            if (Math.Abs(Settings.ToolbarOpacity * 100 - clamped) < 0.01)
            {
                return;
            }

            Settings.ToolbarOpacity = clamped / 100.0;
            _service?.ApplyOpacityToWindow();
            Save(nameof(OpacityText), nameof(OpacitySummary));
        }
    }

    public string OpacityText => $"{Settings.ToolbarOpacity * 100:F0}%";

    public string OpacitySummary =>
        Settings.ToolbarOpacity >= 0.999
            ? "100% 完全不透明。"
            : $"当前 {Settings.ToolbarOpacity * 100:F0}%：压住课件的时候能透过去看见底下，" +
              "又不像「隐藏」那样找不回来。";

    public string SizeSummary =>
        $"直径 {Settings.Diameter:F0}（{ToolboxSettings.MinDiameter}~{ToolboxSettings.MaxDiameter} 可调）。" +
        "按钮高度、背景板、大字字号都按这个数等比缩放。";

    #endregion

    #region 名单与幸运抽签

    public string RosterSummary => _service is null
        ? "插件未就绪。"
        : $"{_service.RosterPath}\n一行一个名字，保存后立即生效。";

    private void OnOpenRoster(object? sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_service.RosterPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 没有关联程序就算了，路径就写在上面。
        }
    }

    private void OnReloadRoster(object? sender, RoutedEventArgs e)
    {
        _service?.Roster?.Reload();
        _service?.NotifyShortcutsChanged();
        Raise(nameof(RosterSummary), nameof(StatsSummary));
    }

    public string StatsSummary
    {
        get
        {
            if (_service is null)
            {
                return "插件未就绪。";
            }

            var names = _service.Roster?.Names ?? (IReadOnlyList<string>)Array.Empty<string>();
            if (names.Count == 0)
            {
                return "名单是空的，还没有什么可统计的。";
            }

            var stats = _service.Stats;
            if (stats.TotalPicks == 0)
            {
                return $"共 {names.Count} 人，还没有幸运抽签记录。";
            }

            var min = stats.MinCount(names);
            var max = stats.MaxCount(names);
            var average = names.Average(stats.CountOf);

            return $"共 {names.Count} 人，累计幸运抽签 {stats.TotalPicks} 次；" +
                   $"最少 {min} 次 / 最多 {max} 次 / 平均 {average:F2} 次。" +
                   (max - min <= 1 ? "（已均衡）" : $"\n还差 {max - min} 次拉平，继续幸运抽签就会慢慢靠近。");
        }
    }

    private void OnShowStats(object? sender, RoutedEventArgs e)
    {
        _service?.ShowStatsWindow();
        Raise(nameof(StatsSummary));
    }

    private void OnResetRound(object? sender, RoutedEventArgs e)
    {
        if (_service?.Roster is null)
        {
            return;
        }

        _service.Roster.ResetRound(_service.Pick, _service.Stats);
        _service.SaveAll();
        _service.NotifyShortcutsChanged();
        Raise(nameof(StatsSummary));
    }

    /// <summary>每一轮第一抽回避最近点过的几个人。</summary>
    public double RoundStartAvoid
    {
        get => Settings.Pick.RoundStartAvoid;
        set
        {
            Settings.Pick.RoundStartAvoid = (int)Math.Round(value);
            Save(nameof(RoundStartAvoidText));
        }
    }

    public string RoundStartAvoidText => Settings.Pick.RoundStartAvoid <= 0
        ? "不回避"
        : $"最近 {Settings.Pick.RoundStartAvoid} 人";

    public double RevealSeconds
    {
        get => Settings.RevealSeconds;
        set
        {
            Settings.RevealSeconds = Math.Round(value, 1);
            Save(nameof(RevealSecondsText));
        }
    }

    public string RevealSecondsText => $"{Settings.RevealSeconds:F1} 秒";

    public bool ShowNotification
    {
        get => Settings.ShowNotification;
        set
        {
            Settings.ShowNotification = value;
            Save(nameof(ShowNotification));
        }
    }

    #endregion

    #region 顺序预览与拖动排序

    /// <summary>
    /// 重画顺序预览。
    /// </summary>
    /// <remarks>
    /// 排布和工具条完全一致：<b>左一半 → 主按钮 → 右一半，奇数时右边多一个</b>，
    /// 而且顺序取自同一个 <see cref="ToolboxSettings.EffectiveOrder"/>——
    /// 预览和工具条用的是同一份顺序，不会两边走样。
    /// <para/>
    /// <b>内置功能也参与排序</b>：「幸运抽签」在预览里同样可以拖，位置和快捷方式一视同仁。
    /// </remarks>
    public void RefreshPreview()
    {
        _previewRow.Children.Clear();
        _previewChips.Clear();

        // 尺寸跟着设置走：改「大小」时预览立刻跟着变，不用装上去才知道多大。
        var diameter = Settings.Diameter;
        _previewBoard.Height = diameter / 2;
        _previewRow.Margin = new Thickness(diameter * 0.18, 0, diameter * 0.18, 0);

        var entries = new List<(string Key, string Icon, string Name, ShortcutItem? Shortcut)>();
        foreach (var key in Settings.EffectiveOrder())
        {
            if (key == ToolboxSettings.LuckyDrawKey)
            {
                entries.Add((key, "\U0001F64B", "幸运抽签", null));
                continue;
            }

            if (key == ToolboxSettings.AnnotateKey)
            {
                entries.Add((key, "\u270F\uFE0F", "批注", null));
                continue;
            }

            if (key == ToolboxSettings.EraserKey)
            {
                entries.Add((key, "\U0001F9FC", "橡皮", null));
                continue;
            }

            var shortcut = Settings.Shortcuts.FirstOrDefault(x => x.Id == key);
            if (shortcut is not null)
            {
                entries.Add((key, shortcut.IconOrDefault, shortcut.ButtonText, shortcut));
            }
        }

        var leftCount = entries.Count / 2;
        _previewRow.Children.Add(BuildPreviewSide(entries.Take(leftCount)));
        _previewRow.Children.Add(BuildPreviewKnob());
        _previewRow.Children.Add(BuildPreviewSide(entries.Skip(leftCount)));
    }

    private Control BuildPreviewSide(
        IEnumerable<(string Key, string Icon, string Name, ShortcutItem? Shortcut)> entries)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var entry in entries)
        {
            panel.Children.Add(BuildPreviewChip(entry.Key, entry.Icon, entry.Name, entry.Shortcut));
        }

        return panel;
    }

    /// <summary>
    /// 预览里的一颗按钮。
    /// </summary>
    /// <remarks>
    /// 图标走和工具条**同一套解析**：指向文件的快捷方式优先用文件本身的图标
    /// （<see cref="FileIconLoader"/>），取不到才退回 emoji。
    /// 之前预览只画 emoji，所以「预览里的图标和装上去不一样」。
    /// </remarks>
    private Border BuildPreviewChip(string key, string icon, string name, ShortcutItem? shortcut)
    {
        var height = Settings.ItemHeight;

        var iconHost = new Control();
        var fileIcon = shortcut is { UseFileIcon: true } ? FileIconLoader.Load(shortcut.Target) : null;

        if (fileIcon is not null)
        {
            iconHost = new Image
            {
                Source = fileIcon,
                Width = height * 0.44,
                Height = height * 0.44,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        else
        {
            iconHost = new TextBlock
            {
                Text = icon,
                FontSize = height * 0.30,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                iconHost,
                new TextBlock
                {
                    Text = name,
                    FontSize = height * 0.21,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };

        var surface = new CloudButtonSurface
        {
            Fill = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF),
            Stroke = Color.FromRgb(0x93, 0xC1, 0xEB),
            StrokeThickness = 1.3
        };

        // 同 ToolboxWindow：留白给内容，别给 Border，否则云会被挤窄。
        content.Margin = new Thickness(height * 0.24, 0, height * 0.24, 0);

        var chip = new Border
        {
            Height = height,
            MinWidth = height,
            Padding = new Thickness(0),
            Background = null,
            BorderThickness = new Thickness(0),
            Child = new Panel { Children = { surface, content } },
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        ToolTip.SetTip(chip, "按住拖动可以调整位置");
        _previewChips.Add((chip, key));
        return chip;
    }

    private Control BuildPreviewKnob()
    {
        var d = Settings.Diameter;

        var label = new TextBlock
        {
            Text = "教学\n助手",
            FontSize = d * 0.26,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3E, 0x54, 0x69)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            RenderTransform = new TranslateTransform(0, d * 0.23),
            Margin = new Thickness(0)
        };

        var surface = new CloudButtonSurface
        {
            Layers =
            [
                new CloudLayer(0.24, 0.02, 0.78, 0.62, 0.92),   // 右上
                new CloudLayer(0.11, 0.34, 0.78, 0.62, 1.00)    // 下方居中
            ],
            Fill = Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF),
            Stroke = Color.FromRgb(0x74, 0xAF, 0xE4),
            StrokeThickness = 1.6
        };

        // 云面比框大一圈，和工具条一致；所以 Width/Height 是 d 的倍数，靠居中溢出。
        surface.Width = d * 1.34;
        surface.Height = d * 1.34;
        surface.HorizontalAlignment = HorizontalAlignment.Center;
        surface.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Width = d,
            Height = d,
            Background = null,
            BorderThickness = new Thickness(0),
            Child = new Panel { Children = { surface, label } },
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// 拖动排序。
    /// </summary>
    /// <remarks>
    /// 事件挂在**预览那一行**上而不是每颗按钮上：拖到一半会重画预览，
    /// 挂在自己身上的话控件被销毁、指针捕获跟着丢，拖到第二格就断了。
    /// 挂在不会动的容器上就一直是同一份捕获。
    /// <para/>
    /// 动的是一整份「顺序表」，内置功能和快捷方式混在一起排，
    /// 所以「幸运抽签」也能被拖到任意位置。
    /// </remarks>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggingIndex = ChipIndexAt(e.GetPosition(_previewRow));
        _previewDragged = false;
        _previewPressPoint = e.GetPosition(_previewRow);

        if (_draggingIndex >= 0)
        {
            e.Pointer.Capture(_previewRow);
        }
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingIndex < 0)
        {
            return;
        }

        var point = e.GetPosition(_previewRow);

        if (!_previewDragged)
        {
            // 和工具条一样：动得太少算点击，不算拖动。
            if (Math.Abs(point.X - _previewPressPoint.X) < 6)
            {
                return;
            }

            _previewDragged = true;
            HighlightDragging();
        }

        var target = TargetIndex(point.X);
        if (target < 0 || target == _draggingIndex)
        {
            return;
        }

        // 先把归一化后的完整顺序落下来，再在它上面挪——不然拖动会作用到一份临时算出来的列表上。
        var order = Settings.EffectiveOrder();
        var moved = order[_draggingIndex];
        order.RemoveAt(_draggingIndex);
        order.Insert(target, moved);
        Settings.SetOrder(order);

        _draggingIndex = target;

        RefreshPreview();
        HighlightDragging();
    }

    private void HighlightDragging()
    {
        if (_draggingIndex >= 0 && _draggingIndex < _previewChips.Count)
        {
            _previewChips[_draggingIndex].Chip.Opacity = 0.55;
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggingIndex >= 0)
        {
            e.Pointer.Capture(null);
        }

        _draggingIndex = -1;
        _previewDragged = false;
        RefreshPreview();
        QueueShortcutSave();
    }

    /// <summary>
    /// 一颗按钮在预览那一行里的矩形。
    /// </summary>
    /// <remarks>
    /// <b>不能直接用 <c>Bounds</c>。</b>它是相对**自己的父级**（左右两侧各一个 StackPanel）的，
    /// 而指针事件的坐标是相对 <see cref="_previewRow"/> 的——两个坐标系差了一层，直接比会永远对不上。
    /// </remarks>
    private Rect BoundsInRow(Control chip) =>
        new(chip.TranslatePoint(new Point(0, 0), _previewRow) ?? default, chip.Bounds.Size);

    /// <summary>指针落在第几颗上（-1 = 没落在任何一颗上）。</summary>
    private int ChipIndexAt(Point point)
    {
        for (var i = 0; i < _previewChips.Count; i++)
        {
            if (BoundsInRow(_previewChips[i].Chip).Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>拖到 x 位置时应该插到第几颗。</summary>
    private int TargetIndex(double x)
    {
        for (var i = 0; i < _previewChips.Count; i++)
        {
            var bounds = BoundsInRow(_previewChips[i].Chip);
            if (x < bounds.X + bounds.Width / 2)
            {
                return i;
            }
        }

        return _previewChips.Count - 1;
    }

    #endregion

    #region 快捷方式

    public System.Collections.ObjectModel.ObservableCollection<ShortcutItem> Shortcuts => Settings.Shortcuts;

    private void OnShortcutsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshPreview();

        if (e.NewItems is not null)
        {
            foreach (ShortcutItem item in e.NewItems)
            {
                item.PropertyChanged += OnShortcutPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ShortcutItem item in e.OldItems)
            {
                item.PropertyChanged -= OnShortcutPropertyChanged;
            }
        }

        QueueShortcutSave();
    }

    private void OnShortcutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 名称、图标一改，预览要立刻跟着变，不然"预览"就没意义了。
        RefreshPreview();
        QueueShortcutSave();
    }

    /// <summary>防抖：连打字的时候不要每个字符都写一次盘。</summary>
    private void QueueShortcutSave()
    {
        _shortcutSaveTimer.Stop();
        _shortcutSaveTimer.Start();
    }

    /// <summary>
    /// 打开系统文件选择框，返回本地路径（用户取消时返回 null）。
    /// </summary>
    /// <remarks>
    /// 走 Avalonia 的 <see cref="IStorageProvider"/>，由宿主所在的平台弹原生对话框。
    /// 拿不到 <see cref="TopLevel"/>（比如页面还没挂到窗口上）就直接放弃，
    /// 让用户手填路径——不能因为选不了文件就把整个设置页卡住。
    /// </remarks>
    private async Task<string?> PickPathAsync(string title, params string[] patterns)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        if (patterns.Length > 0)
        {
            options.FileTypeFilter =
            [
                new FilePickerFileType("匹配的文件") { Patterns = patterns },
                // 一定要留一条「所有文件」：Windows 上只给 *.exe 的话，
                // 用户想选的 .lnk / 无扩展名脚本会被对话框直接藏掉。
                new FilePickerFileType("所有文件") { Patterns = ["*"] }
            ];
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        return files?.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnBrowseProgram(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShortcutItem item)
        {
            return;
        }

        var path = await PickPathAsync("选择要启动的程序",
            ["*.exe", "*.bat", "*.cmd", "*.com", "*.lnk", "*.ps1", "*.sh"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            item.Target = path;
            QueueShortcutSave();
        }
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShortcutItem item)
        {
            return;
        }

        var path = await PickPathAsync("选择要打开的文件");
        if (!string.IsNullOrWhiteSpace(path))
        {
            item.Target = path;
            QueueShortcutSave();
        }
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShortcutItem item)
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择文件夹",
            AllowMultiple = false
        });

        var path = folders?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            item.Target = path;
            QueueShortcutSave();
        }
    }

    #region 打开文件：三种路径方式

    /// <summary>
    /// 相对路径的基准目录和内部副本目录，在快捷方式区顶部统一说明一次。
    /// </summary>
    /// <remarks>
    /// 这两个路径如果逐条显示在每个快捷方式下面会很啰嗦，所以只在这里说一遍。
    /// </remarks>
    public string PathModeBaseHint
    {
        get
        {
            var app = ShortcutPathResolver.AppRoot;
            var inner = ShortcutPathResolver.InternalRoot;

            var appText = string.IsNullOrWhiteSpace(app) ? "（暂时拿不到 ClassIsland 数据根目录）" : app;
            var innerText = string.IsNullOrWhiteSpace(inner) ? "（插件数据目录还没就绪）" : inner;

            return "「打开文件或文件夹」有三种路径方式：绝对路径照原样打开；"
                 + $"相对路径以「{appText}」为基准解析；"
                 + $"插件内部副本存放在「{innerText}」，只接受视频 / 图片 / Office 单文件。";
        }
    }

    /// <summary>内部目录现在存了多少东西。</summary>
    public string InternalFolderSummary
    {
        get
        {
            var files = ShortcutPathResolver.ListInternalFiles();
            if (files.Count == 0)
            {
                return "内部目录还是空的。";
            }

            var total = 0L;
            foreach (var file in files)
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // 单个文件读不到就跳过，不影响总数显示。
                }
            }

            return $"内部目录现有 {files.Count} 个副本，共 {FormatSize(total)}。";
        }
    }

    private string _shortcutActionMessage = "";

    /// <summary>上一次「保存副本 / 清理」的结果，直接显示在按钮下面。</summary>
    public string ShortcutActionMessage
    {
        get => _shortcutActionMessage;
        private set
        {
            if (_shortcutActionMessage == value)
            {
                return;
            }

            _shortcutActionMessage = value;
            Raise(nameof(ShortcutActionMessage), nameof(HasShortcutActionMessage));
        }
    }

    public bool HasShortcutActionMessage => !string.IsNullOrEmpty(_shortcutActionMessage);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };

    /// <summary>
    /// 把内部目录钉到插件配置目录下。
    /// </summary>
    /// <remarks>
    /// <c>ToolboxPlugin.Initialize</c> 里已经钉过一次了；这里再来一次是因为
    /// 设置页可能在别的时机被创建（而且 <c>--serve-only</c> 之类的调试路径不一定走过 Initialize），
    /// 重复设置是幂等的，不会出问题。
    /// </remarks>
    private void SyncInternalRoot()
    {
        if (_service is not null)
        {
            ShortcutPathResolver.EnsureInternalRoot(_service.ConfigFolder);
        }
    }

    /// <summary>选一个文件，换算成相对 ClassIsland 数据根目录的路径填进去。</summary>
    private async void OnBrowseFileRelative(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShortcutItem item)
        {
            return;
        }

        var path = await PickPathAsync("选择要打开的文件（会换算成相对路径）");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var relative = ShortcutPathResolver.TryMakeAppRelative(path);
        if (relative is null)
        {
            // 相对化不了就**不要**偷偷填成绝对路径——用户会以为已经生效了，
            // 换台机器才发现指向的还是原来那台。
            ShortcutActionMessage =
                "这个文件不在 ClassIsland 数据根目录里，没法写成相对路径：\n" +
                $"{path}\n基准目录是：{ShortcutPathResolver.AppRoot}\n" +
                "想让它跟着插件走，把「路径方式」改成「插件内部副本」，再点「保存副本…」。";
            return;
        }

        item.Target = relative;
        QueueShortcutSave();
        ShortcutActionMessage = $"已转成相对路径：{relative}";
    }

    /// <summary>把选中的文件复制一份到插件内部目录，目标里只存副本文件名。</summary>
    private async void OnStoreInternalCopy(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ShortcutItem item)
        {
            return;
        }

        SyncInternalRoot();

        var path = await PickPathAsync("选择要复制进插件的文件（视频 / 图片 / Office 单文件）",
            ShortcutPathResolver.PickerPatterns);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // 对话框虽然带了扩展名过滤，但用户还能切到「所有文件」，所以复制前再拦一道。
        if (!ShortcutPathResolver.IsSupportedInternalFile(path))
        {
            ShortcutActionMessage =
                $"这个类型不能存进插件内部：{Path.GetExtension(path)}\n" +
                "只收视频 / 图片 / Office 单文件（mp4 / png / pptx / pdf 之类）。";
            return;
        }

        try
        {
            ShortcutActionMessage = "正在复制，文件大的话要等一会儿…";

            // 几百兆的视频按同步复制会把设置页冻住，丢到线程池去。
            var stored = await Task.Run(() => ShortcutPathResolver.CopyIntoInternal(path));

            item.Target = stored;
            QueueShortcutSave();
            Raise(nameof(InternalFolderSummary));
            ShortcutActionMessage = $"已保存副本：{stored}\n原文件：{path}";
        }
        catch (Exception ex)
        {
            ShortcutActionMessage = "保存副本失败：" + ex.Message;
        }
    }

    private void OnOpenInternalFolder(object? sender, RoutedEventArgs e)
    {
        SyncInternalRoot();

        var root = ShortcutPathResolver.InternalRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            ShortcutActionMessage = "插件数据目录还没就绪，稍后再试。";
            return;
        }

        try
        {
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
            ShortcutActionMessage = "已打开：" + root;
        }
        catch (Exception ex)
        {
            // 打不开就算了，路径本来就已经写在上面了。
            ShortcutActionMessage = $"打不开目录（{ex.Message}），路径是：{root}";
        }
    }

    private void OnCleanupInternalFiles(object? sender, RoutedEventArgs e)
    {
        SyncInternalRoot();

        var unused = ShortcutPathResolver.FindUnusedInternalFiles(Settings.Shortcuts);
        if (unused.Count == 0)
        {
            Raise(nameof(InternalFolderSummary));
            ShortcutActionMessage = "没有多余的副本，内部目录很干净。";
            return;
        }

        var freed = 0L;
        var removed = 0;
        foreach (var file in unused)
        {
            try
            {
                freed += new FileInfo(file).Length;
                File.Delete(file);
                removed++;
            }
            catch (Exception)
            {
                // 正被播放器/Office 占用就跳过，下次再说。
            }
        }

        Raise(nameof(InternalFolderSummary));
        ShortcutActionMessage = removed == 0
            ? "有副本正被其它程序占用，这次没能删掉。"
            : $"已清理 {removed} 个没人引用的副本，腾出 {FormatSize(freed)}。";
    }

    #endregion

    private void OnAddShortcut(object? sender, RoutedEventArgs e)
    {
        Shortcuts.Add(new ShortcutItem { Name = "新快捷方式", Kind = ShortcutKind.Url, Target = "https://" });
        // 图标留空即可，按钮上会自动按类型显示默认图标。
        QueueShortcutSave();
    }

    /// <summary>
    /// 一键加几条教室里真用得上的。
    /// </summary>
    /// <remarks>
    /// 都是系统自带、不需要额外安装的东西，而且<b>路径写的是可执行文件名</b>——
    /// 交给 <c>UseShellExecute</c> 从 PATH 里找，不用担心装在哪。
    /// ClassIsland 自己的功能则用 <c>classisland://</c> 地址调起
    /// （这些路径是从宿主源码的 <c>HandleAppNavigation</c> 注册里抄来的，写错了不会报错，只会没反应）。
    /// </remarks>
    private void OnAddPresetShortcuts(object? sender, RoutedEventArgs e)
    {
        ShortcutItem[] presets =
        [
            new() { Name = "浏览器", Icon = "🌐", Kind = ShortcutKind.Url, Target = "https://www.classisland.tech" },
            new() { Name = "记事本", Icon = "📝", Kind = ShortcutKind.Command, Target = "notepad.exe" },
            new() { Name = "计算器", Icon = "🧮", Kind = ShortcutKind.Command, Target = "calc.exe" },
            new() { Name = "截图", Icon = "✂", Kind = ShortcutKind.Command, Target = "snippingtool.exe" },
            new()
            {
                Name = "CI设置", Icon = "⚙", Kind = ShortcutKind.ClassIslandUri,
                Target = "classisland://app/settings"
            },
            new()
            {
                Name = "档案", Icon = "📄", Kind = ShortcutKind.ClassIslandUri,
                Target = "classisland://app/profile"
            }
        ];

        foreach (var preset in presets)
        {
            if (Shortcuts.Any(x => x.Name == preset.Name))
            {
                continue;
            }

            Shortcuts.Add(preset);
        }

        QueueShortcutSave();
    }

    private void OnDeleteShortcut(object? sender, RoutedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Button)?.Tag is ShortcutItem item)
        {
            Shortcuts.Remove(item);
            QueueShortcutSave();
        }
    }

    private void OnMoveShortcutUp(object? sender, RoutedEventArgs e) => MoveShortcut(sender, -1);

    private void OnMoveShortcutDown(object? sender, RoutedEventArgs e) => MoveShortcut(sender, 1);

    private void MoveShortcut(object? sender, int delta)
    {
        if ((sender as Avalonia.Controls.Button)?.Tag is not ShortcutItem item)
        {
            return;
        }

        var index = Shortcuts.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Shortcuts.Count)
        {
            return;
        }

        Shortcuts.Move(index, target);
        QueueShortcutSave();
    }

    #endregion

    /// <summary>写盘并通知界面。数值类设置改完都走这儿。</summary>
    private void Save(params string[] alsoChanged)
    {
        _service?.SaveSettings();
        Raise(alsoChanged);
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
}
