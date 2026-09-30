// 教学助手 v1.0.0：ClassIsland 置顶工具条插件
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Timers;
using ClassIsland.Toolbox.Models;

namespace ClassIsland.Toolbox.Services;

/// <summary>
/// 名单的读取与抽选。
/// </summary>
/// <remarks>
/// 名单就是一个纯文本文件，一行一个名字。没有引号、没有 JSON、没有转义，
/// 用记事本就能改。空行和以常见注释符号开头的批注行会被忽略，重复的名字只算一个。
/// <para/>
/// <b>抽选有两层约束，顺序不能反：</b>
/// <list type="number">
/// <item><b>先保轮次</b>——本轮抽过的人不再进候选，一轮之内每人恰好被抽到一次；</item>
/// <item><b>再按次数排</b>——候选里只留累计次数最少的那一档，再等概率随机取一个。</item>
/// </list>
/// 先按次数挑的话，本轮已经抽过的人可能被再抽一次，轮次的保证立刻失效。
/// <para/>
/// 名单很短（几十人），但<b>成员判定走哈希集</b>：每次抽选都要拿"本轮已抽"去比一遍，
/// 用列表线性查会让每次抽选都变成平方级的比较。名单短不等于可以随便写。
/// </remarks>
public class RosterService : IDisposable
{
    /// <summary>「最近抽过的人」最多记多少个。回避窗口最大 5，留十来个足够了。</summary>
    private const int RecentPicksCapacity = 12;

    /// <summary>回避窗口的上限（和设置页滑动条的范围一致）。</summary>
    private const int MaxAvoidWindow = 5;

    /// <summary>文件变动后等多久再读。记事本保存会连着触发好几次。</summary>
    private const double DebounceMilliseconds = 180;

    /// <summary>
    /// 名单文件第一行的声明行。
    /// </summary>
    /// <remarks>
    /// 它自己也是一条批注（以 <c>///</c> 开头），写出来是给老师看的——
    /// 顺手告诉他们这个文件里的批注该怎么写。解析时按普通批注跳过。
    /// </remarks>
    public const string DeclarationLine =
        "///这一行是声明行 后面批注行请使用 /// 或 # 或 // 或 ; 等常见注释符号";

    /// <summary>
    /// 认得的批注前缀。
    /// </summary>
    /// <remarks>
    /// <b>不限定一种。</b>名单多半是从别处拷来的——可能来自脚本、配置文件或者另一份表格，
    /// 那边习惯用什么注释符号都有可能。多认几种的代价只是"某个名字恰好以这些符号开头时会被当批注"，
    /// 而中文姓名不可能以这些符号开头；反过来，只认一种就会让老师拷进来的名单
    /// 凭空多出一堆叫「# 张三」的假名字，那种错误很难自己看出来。
    /// </remarks>
    public static readonly IReadOnlyList<string> CommentPrefixes =
        ["///", "//", "#", ";", "--", "%", "*", "<!--"];

    private readonly string _rosterPath;
    private readonly object _watchGate = new();

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private List<string> _names = [];

    public RosterService(string rosterPath)
    {
        _rosterPath = rosterPath;
        CreateDefaultFileIfMissing();
        Reload();
        StartWatching();
    }

    /// <summary>名单文件的完整路径。</summary>
    public string RosterPath => _rosterPath;

    /// <summary>当前名单。</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>名单文件发生变化并重新载入后触发。</summary>
    public event EventHandler? RosterChanged;

    /// <summary>这一行是不是批注。</summary>
    private static bool IsComment(string line)
    {
        foreach (var prefix in CommentPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>名单文件不存在时铺一份带说明的模板出去，省得用户不知道格式。</summary>
    private void CreateDefaultFileIfMissing()
    {
        if (File.Exists(_rosterPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_rosterPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(_rosterPath,
            [
                DeclarationLine,
                "///一行一个名字，保存后自动生效。",
                "///空行、以及以 /// 或 # 或 // 或 ; 等常见注释符号开头的行都会被忽略。",
                "",
                "张三",
                "李四",
                "王五"
            ], new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 铺不出来也不影响后面读——读不到就是空名单，用户可以在设置页里手动建。
        }
    }

    /// <summary>
    /// 重新读取名单文件。
    /// </summary>
    /// <remarks>
    /// 读不出来（被占用、没权限）时<b>保留上一次的名单</b>：
    /// 清空的话界面上的名单会毫无理由地消失一下，用户会以为文件被删了。
    /// </remarks>
    public void Reload()
    {
        string[] lines;
        try
        {
            lines = File.Exists(_rosterPath) ? File.ReadAllLines(_rosterPath, Encoding.UTF8) : [];
        }
        catch (Exception)
        {
            return;
        }

        // 去重时保序：先出现的名字排在前面，界面上看到的就是文件里的顺序。
        var names = new List<string>(lines.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var name = line.Trim();
            if (name.Length == 0 || IsComment(name))
            {
                continue;
            }

            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        _names = names;
    }

    #region 文件监视

    private void StartWatching()
    {
        var directory = Path.GetDirectoryName(_rosterPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_rosterPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;

            // 一次保存会连着来好几个事件，用防抖计时器合并成一次读。
            // **不要用 Thread.Sleep**：文件监视的回调跑在线程池线程上，
            // 在那里睡觉会占着线程不放，事件一密就开始丢。
            _debounce = new Timer(DebounceMilliseconds) { AutoReset = false };
            _debounce.Elapsed += OnDebounceElapsed;

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // 监视不起来就算了，菜单里还有「重新载入名单」这条退路。
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        lock (_watchGate)
        {
            if (_debounce is null)
            {
                return;
            }

            // 每次事件都把计时器推后一点，直到安静下来才真的去读。
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private void OnDebounceElapsed(object? sender, ElapsedEventArgs e)
    {
        Reload();
        RosterChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileTouched;
            _watcher.Created -= OnFileTouched;
            _watcher.Renamed -= OnFileTouched;
            _watcher.Dispose();
            _watcher = null;
        }

        lock (_watchGate)
        {
            if (_debounce is not null)
            {
                _debounce.Elapsed -= OnDebounceElapsed;
                _debounce.Dispose();
                _debounce = null;
            }
        }
    }

    #endregion

    #region 抽选

    /// <summary>
    /// 抽一个人。
    /// </summary>
    /// <param name="settings">幸运抽签设置，本轮已抽 / 最近抽过会就地更新。</param>
    /// <param name="stats">次数历史，抽中的人会在这里记上一笔。</param>
    /// <returns>抽中的名字；名单为空时返回 <c>null</c>。</returns>
    public string? Pick(PickSettings settings, PickStats stats)
    {
        if (_names.Count == 0)
        {
            return null;
        }

        if (_names.Count == 1)
        {
            // 只有一个人时不必进候选池那套逻辑，但账还是要记。
            Remember(settings, stats, _names[0]);
            return _names[0];
        }

        var pool = BuildPool(settings, stats);
        var picked = pool[RandomNumberGenerator.GetInt32(pool.Count)];
        Remember(settings, stats, picked);
        return picked;
    }

    /// <summary>手动开始新一轮：丢掉本轮进度，累计次数和历史记录都保留。</summary>
    public void ResetRound(PickSettings settings, PickStats stats)
    {
        settings.DrawnThisRound.Clear();
        stats.StartNewRound();
    }

    /// <summary>本轮还剩多少人没抽到。</summary>
    public int RemainingInRound(PickSettings settings)
    {
        var drawn = new HashSet<string>(settings.DrawnThisRound ?? [], StringComparer.Ordinal);
        var left = 0;

        foreach (var name in _names)
        {
            if (!drawn.Contains(name))
            {
                left++;
            }
        }

        return left;
    }

    /// <summary>
    /// 组出这一抽的候选池。
    /// </summary>
    /// <remarks>
    /// <b>最后一步永远是在池子里等概率随机取一个</b>，取值走操作系统熵源，
    /// 不带种子、不按名单顺序、不用姓名或序号做任何 tie-break ——
    /// 次数相同的那一档人里，谁先谁后是纯随机的。
    /// </remarks>
    private List<string> BuildPool(PickSettings settings, PickStats stats)
    {
        var drawn = new HashSet<string>(settings.DrawnThisRound ?? [], StringComparer.Ordinal);

        var remaining = new List<string>(_names.Count);
        foreach (var name in _names)
        {
            if (!drawn.Contains(name))
            {
                remaining.Add(name);
            }
        }

        if (remaining.Count == 0)
        {
            // 一轮抽满了：自动开新一轮。累计次数和历史都留着，只清本轮进度。
            settings.DrawnThisRound.Clear();
            stats.StartNewRound();
            remaining.AddRange(_names);
        }

        ApplyRoundStartAvoidance(settings, remaining);

        // 只留累计次数最少的那一档：**边扫边收**，
        // 不必先求一遍最小值再筛一遍——名单虽短，两遍也是白跑。
        var pool = new List<string>();
        var lowest = int.MaxValue;

        foreach (var name in remaining)
        {
            var count = stats.CountOf(name);
            if (count < lowest)
            {
                lowest = count;
                pool.Clear();
                pool.Add(name);
            }
            else if (count == lowest)
            {
                pool.Add(name);
            }
        }

        return pool.Count > 0 ? pool : remaining;
    }

    /// <summary>
    /// 每一轮的第一抽，回避最近抽过的几个人。
    /// </summary>
    /// <remarks>
    /// 判据是「这一轮一个人都还没抽」（剩余人数等于名单人数），
    /// 这样自动翻轮和手动「开始新一轮」走的是同一条路。
    /// <para/>
    /// <b>回避的只是顺序，不是资格</b>：被回避的人本轮之内照样会被抽到一次。
    /// 所以先算出一个"最多能回避几个"的预算，再一次性筛掉，
    /// 无论窗口设成几都保证至少留一个人可选，绝不会出现抽不出人的情况。
    /// </remarks>
    private void ApplyRoundStartAvoidance(PickSettings settings, List<string> remaining)
    {
        var window = Math.Clamp(settings.RoundStartAvoid, 0, MaxAvoidWindow);
        if (window <= 0 || remaining.Count != _names.Count)
        {
            return;
        }

        var recent = settings.RecentPicks;
        if (recent is null || recent.Count == 0)
        {
            return;
        }

        // 预算卡在"至少要剩一个人"上。名单只剩一个人时预算为 0，直接不回避。
        var budget = Math.Min(window, remaining.Count - 1);
        if (budget <= 0)
        {
            return;
        }

        var present = new HashSet<string>(remaining, StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);

        // RecentPicks 是"最近的排在前面"，所以先被回避的就是最近抽过的那几个。
        foreach (var name in recent)
        {
            if (excluded.Count >= budget)
            {
                break;
            }

            // 只有确实还在名单里的人才占名额：历史里可能留着已经删掉的名字，
            // 让他们白占一个名额的话，回避窗口就形同虚设。
            if (present.Contains(name))
            {
                excluded.Add(name);
            }
        }

        if (excluded.Count > 0)
        {
            remaining.RemoveAll(excluded.Contains);
        }
    }

    /// <summary>把一次抽中的结果记到设置和历史里。</summary>
    private static void Remember(PickSettings settings, PickStats stats, string picked)
    {
        if (!settings.DrawnThisRound.Contains(picked, StringComparer.Ordinal))
        {
            settings.DrawnThisRound.Add(picked);
        }

        settings.LastPicked = picked;
        RememberRecently(settings, picked);
        stats.Record(picked);
    }

    /// <summary>
    /// 把 <paramref name="picked"/> 挪到「最近抽过」的最前面，并保持长度有上限。
    /// </summary>
    /// <remarks>
    /// <b>先去重再插头部。</b>同一个人反复抽到时不能占多个位置——
    /// 否则回避窗口会被他一个人的名字挤满，等于没回避。
    /// </remarks>
    private static void RememberRecently(PickSettings settings, string picked)
    {
        var recent = settings.RecentPicks ??= [];

        for (var i = recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(recent[i], picked, StringComparison.Ordinal))
            {
                recent.RemoveAt(i);
            }
        }

        recent.Insert(0, picked);

        var excess = recent.Count - RecentPicksCapacity;
        if (excess > 0)
        {
            recent.RemoveRange(RecentPicksCapacity, excess);
        }
    }

    #endregion
}
