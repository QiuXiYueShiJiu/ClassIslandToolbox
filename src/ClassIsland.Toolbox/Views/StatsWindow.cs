using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Toolbox.Models;
using ClassIsland.Toolbox.Services;

namespace ClassIsland.Toolbox.Views;

/// <summary>
/// 「抽取次数统计」窗口：一张饼状图 + 一份按次数排序的明细表。
/// </summary>
/// <remarks>
/// 从悬浮窗右键菜单里的「查看抽取次数（饼状图）」打开。
/// <para/>
/// 几个刻意的设计：
/// <list type="bullet">
/// <item><b>颜色按名单顺序分配，不按次数排序分配。</b>否则每抽一次名次一变，
///       同一个人的颜色就跟着跳，饼图看着像在闪。</item>
/// <item><b>窗口开着的时候会自己刷新</b>（2 秒一次）。上课时一边抽一边把统计开着，
///       数字要跟着动才有用；不想让它一直重画的话关掉窗口就行。</item>
/// <item><b>「清除统计」要点两次</b>。这是破坏性操作，又是个只有一行的按钮，
///       误点的代价是整学期的记录没了，值得多问一句。</item>
/// </list>
/// </remarks>
public sealed class StatsWindow : Window
{
    private static StatsWindow? _instance;

    private readonly PickSettings _settings;
    private readonly RosterService _roster;
    private readonly PickStats _stats;
    private readonly Action _persist;

    private readonly PieChartControl _chart = new();
    private readonly TextBlock _emptyHint;
    private readonly TextBlock _headline = new();
    private readonly TextBlock _fairness = new();
    private readonly TextBlock _roundLine = new();
    private readonly StackPanel _legend = new();
    private readonly Button _clearButton;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>「清除统计」的二次确认状态。</summary>
    private bool _confirmingClear;

    /// <summary>上一次刷新时的数据指纹，用来跳过没必要的重画。</summary>
    private (int TotalPicks, int Rounds, int Drawn, int RosterCount, string? LastPicked)? _lastSignature;

    private DispatcherTimer? _confirmTimer;

    /// <summary>
    /// 打开统计窗口。已经开着就把它提到前面并刷新。
    /// </summary>
    /// <param name="persist">清除统计之后要把设置和历史写回磁盘。</param>
    public static void Show(PickSettings settings, RosterService roster, PickStats stats, Action persist)
    {
        if (_instance is { IsVisible: true } existing)
        {
            existing.Refresh();
            existing.Activate();
            return;
        }

        var window = new StatsWindow(settings, roster, stats, persist);
        _instance = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_instance, window))
            {
                _instance = null;
            }
        };
        window.Show();
    }

    private StatsWindow(PickSettings settings, RosterService roster, PickStats stats, Action persist)
    {
        _settings = settings;
        _roster = roster;
        _stats = stats;
        _persist = persist;

        ConfigureWindow();

        _emptyHint = new TextBlock
        {
            Text = "还没有抽签记录\n抽一次就出现了",
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
            IsVisible = false
        };

        _clearButton = new Button { Content = "清除统计" };
        _clearButton.Click += OnClearClicked;

        Content = ComposeLayout();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => Refresh();

        Refresh();
    }

    /// <summary>窗口本身的外观。跟内容无关，单独放一处。</summary>
    private void ConfigureWindow()
    {
        Title = "幸运抽签统计";
        Width = 940;
        Height = 660;
        MinWidth = 760;
        MinHeight = 500;
        CanResize = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 不置顶：它是被叫出来看一眼的，不该压住别的东西。
        Topmost = false;
    }

    #region 界面搭建

    /// <summary>
    /// 整体三行：标题、主区（占比图 + 明细表）、底栏。
    /// </summary>
    /// <remarks>
    /// 拆成几个小方法而不是堆一个巨大的对象初始化器：布局改一处就得在几十行嵌套里找位置，
    /// 而且哪块归哪块全靠缩进看。<b>每个方法只负责一块，改哪块进哪个方法。</b>
    /// </remarks>
    private Control ComposeLayout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        root.Children.Add(BuildTitleBlock());

        var main = BuildMainArea();
        Grid.SetRow(main, 1);
        root.Children.Add(main);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private static Control BuildTitleBlock()
    {
        var block = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 0),
            Spacing = 4
        };

        block.Children.Add(new TextBlock
        {
            Text = "幸运抽签记录",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold
        });

        block.Children.Add(new TextBlock
        {
            Text = "每抽中一个人这里就记一笔，历史存在插件配置目录的「幸运抽签统计.json」里。",
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap
        });

        return block;
    }

    /// <summary>主区：左边占比图，右边明细表。</summary>
    private Control BuildMainArea()
    {
        var main = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(20, 12, 20, 8)
        };

        main.Children.Add(BuildChartSide());

        var legend = BuildLegendSide();
        Grid.SetColumn(legend, 1);
        main.Children.Add(legend);

        return main;
    }

    /// <summary>左半边：饼图，加上图下面那三行文字。</summary>
    private Control BuildChartSide()
    {
        // 图是 1:1 的，给个固定方框；空记录时的提示压在同一个框里。
        var chartArea = new Panel { Width = 380, Height = 380 };
        chartArea.Children.Add(_chart);
        chartArea.Children.Add(_emptyHint);

        // 这三行都会换行，样式一致，一起设。
        foreach (var line in new[] { _headline, _fairness, _roundLine })
        {
            line.TextWrapping = TextWrapping.Wrap;
            line.HorizontalAlignment = HorizontalAlignment.Center;
            line.TextAlignment = TextAlignment.Center;
        }

        _headline.FontSize = 14;
        _fairness.FontSize = 13;
        _fairness.Opacity = 0.85;
        _roundLine.FontSize = 13;
        _roundLine.Opacity = 0.72;

        var side = new StackPanel { Width = 380, Spacing = 14 };

        side.Children.Add(new TextBlock
        {
            Text = "被抽次数占比",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        side.Children.Add(chartArea);
        side.Children.Add(_headline);
        side.Children.Add(_fairness);
        side.Children.Add(_roundLine);

        return side;
    }

    /// <summary>右半边：表头固定、下面一张可滚的明细表。</summary>
    private Control BuildLegendSide()
    {
        var list = new ScrollViewer
        {
            Content = _legend,
            Padding = new Thickness(0, 4, 8, 4),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        var side = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            // 跟左边的图拉开一点距离，两块才分得清。
            Margin = new Thickness(28, 0, 0, 0)
        };

        side.Children.Add(BuildLegendHeader());
        Grid.SetRow(list, 1);
        side.Children.Add(list);

        return side;
    }

    private Control BuildFooter()
    {
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(20, 0, 20, 16)
        };

        footer.Children.Add(_clearButton);

        var refresh = new Button { Content = "刷新" };
        refresh.Click += (_, _) => Refresh();
        footer.Children.Add(refresh);

        var close = new Button { Content = "关闭" };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);

        return footer;
    }

    /// <summary>明细表的列：色块 / 姓名条 / 次数 / 占比 / 本轮。</summary>
    private const string TableColumns = "18,220,Auto,Auto,Auto";

    private static Control BuildLegendHeader()
    {
        var header = NewTableRow(0, 2);

        AddText(header, "姓名（条形长度＝次数多少）", 1, 0.55, FontWeight.SemiBold);
        AddText(header, "次数", 2, 0.55, FontWeight.SemiBold, HorizontalAlignment.Right);
        AddText(header, "占比", 3, 0.55, FontWeight.SemiBold, HorizontalAlignment.Right);
        AddText(header, "本轮", 4, 0.55, FontWeight.SemiBold, HorizontalAlignment.Right);

        return header;
    }

    /// <summary>建一行表格容器。行距按位置给：表头紧凑，数据行松一点。</summary>
    private static Grid NewTableRow(double top, double bottom) => new()
    {
        ColumnDefinitions = new ColumnDefinitions(TableColumns),
        Margin = new Thickness(0, top, 0, bottom)
    };

    /// <summary>往指定列放一段文字。</summary>
    private static void AddText(Grid row, string text, int column, double opacity,
        FontWeight weight = default, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = opacity,
            FontWeight = weight == default ? FontWeight.Normal : weight,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 前两列是色块和长条，本身有位置；后面几列是数字，
        // 统一留左边距并给个最小宽度——不给的话位数一变整列就会左右跳。
        if (column > 1)
        {
            block.Margin = new Thickness(10, 0, 0, 0);
            block.MinWidth = column == 2 ? 56 : 60;
        }

        row.Children.Add(InColumn(block, column));
    }

    /// <summary>明细表的一行：色块、姓名长条、次数、占比、本轮状态。</summary>
    private Control BuildRow(PickStatsRow row, Color color, int maxCount)
    {
        var line = NewTableRow(3, 3);
        var accent = new SolidColorBrush(color);

        line.Children.Add(InColumn(new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(3),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center
        }, 0));

        line.Children.Add(InColumn(BuildNameBar(row, color, maxCount), 1));

        AddText(line, row.Count.ToString(CultureInfo.InvariantCulture), 2, 1.0,
            FontWeight.SemiBold, HorizontalAlignment.Right);
        AddText(line, ChartPalette.Percent(row.Share), 3, 0.7, default, HorizontalAlignment.Right);

        line.Children.Add(InColumn(new TextBlock
        {
            Text = row.DrawnThisRound ? "已抽" : "待抽",
            FontSize = 12,
            Opacity = row.DrawnThisRound ? 0.45 : 0.85,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 56,
            Margin = new Thickness(10, 0, 0, 0)
        }, 4));

        return line;
    }

    /// <summary>
    /// 姓名那一格：一条按比例定长的底色，名字压在它上面。
    /// </summary>
    /// <remarks>
    /// 底色和名字<b>必须叠在同一格里</b>，不能拆成两列：
    /// 拆开的话名字会跟在底色后面走，短条上的名字就飘到中间去了。
    /// </remarks>
    private static Control BuildNameBar(PickStatsRow row, Color color, int maxCount)
    {
        var ratio = maxCount <= 0 ? 0.0 : row.Count / (double)maxCount;
        var host = new Panel { Height = 20, Width = 210 };

        host.Children.Add(new Border
        {
            Height = 20,
            Width = Math.Max(2, ratio * 200),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color, row.Count > 0 ? 0.26 : 0.10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        });

        host.Children.Add(new TextBlock
        {
            Text = row.InRoster ? row.Name : row.Name + "（已移出名单）",
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Opacity = row.InRoster ? 1.0 : 0.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        return host;
    }

    /// <summary>把控件放进指定列。包一层是为了少写一遍 <c>Grid.SetColumn</c> 再 <c>Add</c>。</summary>
    private static T InColumn<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    #endregion

    #region 刷新

    /// <summary>重新读一遍名单和历史，把图、表、汇总文字全部重画。</summary>
    public void Refresh()
    {
        // 定时刷新每 2 秒跑一次，数据没变就别白重建几百个控件（还会打断正在拖的滚动条）。
        var signature = (_stats.TotalPicks, _stats.Rounds, _settings.DrawnThisRound.Count,
            _roster.Names.Count, _settings.LastPicked);
        if (signature == _lastSignature && _legend.Children.Count > 0)
        {
            return;
        }

        _lastSignature = signature;

        var rows = _stats.BuildRows(_roster.Names, _settings.DrawnThisRound);
        var maxCount = rows.Count == 0 ? 0 : rows.Max(x => x.Count);
        var inRoster = rows.Where(x => x.InRoster).ToList();

        var colors = BuildColorMap();

        _chart.Slices = rows
            .Where(x => x.Count > 0)
            .Select(x => new PieSlice(x.Name, x.Count, colors[x.Name]))
            .ToList();

        var hasData = _stats.TotalPicks > 0;
        _emptyHint.IsVisible = !hasData;
        _chart.Opacity = hasData ? 1.0 : 0.35;

        _legend.Children.Clear();
        if (rows.Count == 0)
        {
            _legend.Children.Add(new TextBlock
            {
                Text = "名单是空的。右键悬浮窗 →「打开名单文件」写几个名字进去。",
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
        else
        {
            foreach (var row in rows)
            {
                _legend.Children.Add(BuildRow(row, colors[row.Name], maxCount));
            }
        }

        var total = _stats.TotalPicks;
        _headline.Text = $"共 {inRoster.Count} 人 · 累计抽取 {total} 次";

        if (inRoster.Count == 0)
        {
            _fairness.Text = string.Empty;
            _roundLine.Text = string.Empty;
        }
        else
        {
            var min = inRoster.Min(x => x.Count);
            var max = inRoster.Max(x => x.Count);
            var average = inRoster.Average(x => x.Count);
            var gap = max - min;

            _fairness.Text = $"最少 {min} 次 · 最多 {max} 次 · 平均 {average:F2} 次    " +
                             (gap <= 1
                                 ? "✓ 已均衡：所有人相差不超过 1 次"
                                 : $"⚠ 相差 {gap} 次，继续抽会慢慢拉平");
        }

        var remaining = _roster.RemainingInRound(_settings);
        _roundLine.Text = $"第 {_stats.Rounds} 轮 · 本轮已抽 " +
                          $"{Math.Max(0, inRoster.Count - remaining)}/{inRoster.Count} 人";

        _refreshTimer.Start();
    }

    /// <summary>
    /// 每个人的颜色。
    /// </summary>
    /// <remarks>
    /// 按<b>名单里的先后顺序</b>分配，所以同一个人的颜色是固定的：
    /// 每抽一次名次就变一次，要是跟着名次分配颜色，整张饼图会一直闪。
    /// 历史里残留下来的、已经不在名单里的人排在后面。
    /// </remarks>
    private Dictionary<string, Color> BuildColorMap()
    {
        var map = new Dictionary<string, Color>(StringComparer.Ordinal);
        var index = 0;

        foreach (var name in _roster.Names)
        {
            if (!map.ContainsKey(name))
            {
                map[name] = ChartPalette.ForIndex(index++);
            }
        }

        foreach (var name in _stats.Counts.Keys
                     .Where(x => !map.ContainsKey(x))
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            map[name] = ChartPalette.ForIndex(index++);
        }

        return map;
    }

    #endregion

    #region 清除统计

    private void OnClearClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_confirmingClear)
        {
            // 第一次点：只改文案，不动数据。
            _confirmingClear = true;
            _clearButton.Content = "再点一次确认清除";
            _confirmTimer?.Stop();
            _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _confirmTimer.Tick += (_, _) =>
            {
                _confirmTimer?.Stop();
                ResetClearButton();
            };
            _confirmTimer.Start();
            return;
        }

        ResetClearButton();

        _stats.ResetAll();
        // 轮次进度和次数是一套账，清了次数却留着「本轮已抽」会很别扭。
        _settings.DrawnThisRound.Clear();
        _settings.LastPicked = null;
        _persist();
        _lastSignature = null;
        Refresh();
    }

    private void ResetClearButton()
    {
        _confirmingClear = false;
        _confirmTimer?.Stop();
        _confirmTimer = null;
        _clearButton.Content = "清除统计";
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _confirmTimer?.Stop();
        base.OnClosed(e);
    }
}
