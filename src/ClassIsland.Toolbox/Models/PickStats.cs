// 教学助手 v1.4.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClassIsland.Toolbox.Models;

/// <summary>
/// 统计表里的一行：某个人的累计被抽次数。
/// </summary>
/// <param name="Name">姓名。</param>
/// <param name="Count">累计被抽到过多少次。</param>
/// <param name="InRoster">这个人是不是还在当前名单里。</param>
/// <param name="DrawnThisRound">本轮是不是已经抽到过了。</param>
/// <param name="Share">占全部抽取次数的比例，0~1。</param>
public sealed record PickStatsRow(
    string Name,
    int Count,
    bool InRoster,
    bool DrawnThisRound,
    double Share);

/// <summary>
/// 抽签历史里的一条：谁、什么时候、第几轮、当时名单多少人、这一抽有没有顺带开启新一轮。
/// </summary>
/// <param name="Id">稳定编号。撤回时按它点名，界面重画多少次都不会认错一条。</param>
/// <param name="Name">抽中的人。</param>
/// <param name="At">抽中的时间（本机时区，带秒）。</param>
/// <param name="StartedNewRound">这一抽是不是把上一轮抽满后自动开启新一轮的那一抽。</param>
/// <param name="Round">这一抽属于第几轮。</param>
/// <param name="RosterSize">抽这一抽时名单上有多少人。</param>
public sealed record PickRecord(
    string Id,
    string Name,
    DateTimeOffset At,
    bool StartedNewRound,
    int Round,
    int RosterSize);

/// <summary>
/// 抽取次数的历史记录 —— 公平性的唯一依据。
/// </summary>
/// <remarks>
/// <b>为什么单独存一份文件，而不是塞进 settings.json：</b>
/// 那些开关丢了重新点一下就行，这份记录丢了就再也算不出「谁被抽得多」。
/// 它落在插件配置目录的 <c>幸运抽签统计.json</c> 里，换名单、改设置、重启都不动它。
/// <para/>
/// <b>逐次历史是唯一真相，次数和「本轮已抽」都是从它算出来的。</b>
/// 这样撤回指定的一抽（不一定是最新那一抽）时，不用去猜「删掉这条之后后面几条怎么办」——
/// 把删完的历史从头重放一遍，次数、轮次、本轮进度、上次抽中、最近抽过就全部对上了。
/// <para/>
/// 重放要从一个起点开始，所以文件里额外存一份 <see cref="Baseline"/>：
/// 历史被截断时，被截掉的那一段的效果（次数、轮次）会先折算进这个起点，再写回去。
/// <para/>
/// 写盘先落临时文件再原子替换：抽人可能连着抽，正好在写的时候断电／被杀进程的话，
/// 直接覆写会把文件截成半截 JSON，历史就全没了。
/// </remarks>
public sealed class PickStats
{
    /// <summary>当前存档格式版本。读旧文件时靠它决定怎么解析。</summary>
    /// <remarks>
    /// 4：历史里每条都带编号、轮次、名单人数，并新增 <c>Baseline</c>；
    /// 撤回不再限制「只能撤最近一抽」，可以按编号撤回任意几条。
    /// 1～3 的文件照常能读：3 版没有 Baseline，读进来时按「当前次数 － 历史累计」倒推。
    /// </remarks>
    private const int CurrentSchema = 4;

    /// <summary>
    /// 逐次历史最多留多少条。
    /// </summary>
    /// <remarks>
    /// 超出的最旧几条会先折算进 <see cref="Baseline"/>（次数、轮次不会丢），
    /// 只是更早的抽签不能再逐条撤回了。一学期几十人、几十轮也就是几百条，
    /// 这个上限足够覆盖「课上抽错了」的场景。
    /// </remarks>
    private const int HistoryCapacity = 800;

    /// <summary>「最近抽过」最多留几个名字。每轮第一抽靠它回避。</summary>
    private const int RecentCapacity = 12;

    /// <summary>一条累计记录：谁、被抽到过几次。</summary>
    private sealed class Entry
    {
        public string Name { get; set; } = "";

        public int Count { get; set; }
    }

    /// <summary>逐次历史的一条。种类是「抽中」或「手动开新一轮」。</summary>
    private sealed class HistoryEntry
    {
        public const string KindPick = "pick";
        public const string KindRound = "round";

        public string Id { get; set; } = "";

        public string Kind { get; set; } = KindPick;

        public string Name { get; set; } = "";

        public DateTimeOffset At { get; set; }

        public bool StartedNewRound { get; set; }

        public int Round { get; set; } = 1;

        public int RosterSize { get; set; }
    }

    /// <summary>历史被截断之后的起点：截掉的那一段折算成的静态状态。</summary>
    private sealed class Baseline
    {
        public List<Entry> Counts { get; set; } = [];

        public int Round { get; set; } = 1;

        public List<string> DrawnThisRound { get; set; } = [];

        public string? LastPicked { get; set; }

        public List<string> RecentPicks { get; set; } = [];
    }

    /// <summary>落盘用的信封。字段名和内部结构解耦，改内部实现不必动存档格式。</summary>
    private sealed class Snapshot
    {
        public int Schema { get; set; } = CurrentSchema;

        public int Round { get; set; } = 1;

        public DateTimeOffset? Updated { get; set; }

        public List<Entry> Picks { get; set; } = [];

        /// <summary>逐次抽取历史，最旧的在前。3 版及以前的文件里没有编号等字段。</summary>
        public List<HistoryEntry>? History { get; set; }

        /// <summary>历史被截断后的起点。3 版及以前的文件里没有。</summary>
        public Baseline? Baseline { get; set; }

        public long NextId { get; set; }
    }

    private Baseline _baseline = new();

    /// <summary>逐次历史，最旧的在前。</summary>
    private readonly List<HistoryEntry> _history = [];

    /// <summary>重放历史得到的累计次数，按名字首次出现的顺序。</summary>
    private readonly List<Entry> _counts = [];

    private readonly List<string> _drawn = [];
    private readonly List<string> _recent = [];

    private long _nextId = 1;
    private int _round = 1;
    private string? _lastPicked;
    private DateTimeOffset? _updated;

    /// <summary>文件里带着 Baseline（4 版存档）时才为 true；否则要等宿主补一次设置状态。</summary>
    private bool _baselineReady;

    /// <summary>是否已经从 settings.json 里补过「本轮已抽 / 上次抽中 / 最近抽过」。</summary>
    private bool _settingsAdopted;

    /// <summary>当前进行到第几轮。小于 1 的值一律按 1 处理。</summary>
    public int Rounds => _round;

    /// <summary>累计抽了多少次。按明细求和，不单独存——省掉一处可能对不上的冗余。</summary>
    public int TotalPicks
    {
        get
        {
            var sum = 0;
            foreach (var pick in _counts)
            {
                sum += pick.Count;
            }

            return sum;
        }
    }

    /// <summary>最后一次改动的时间，纯为了一眼看出数据新旧。</summary>
    public DateTimeOffset? UpdatedAt => _updated;

    /// <summary>本轮已经抽到过的人（按抽到的先后）。</summary>
    public IReadOnlyList<string> DrawnThisRound => _drawn;

    /// <summary>上一次抽中的人。没有记录时是 <c>null</c>。</summary>
    public string? LastPicked => _lastPicked;

    /// <summary>最近抽到过的人，最近的排在前面，最多 12 个。</summary>
    public IReadOnlyList<string> RecentPicks => _recent;

    /// <summary>
    /// 姓名 → 次数 的快照视图。
    /// </summary>
    /// <remarks>
    /// 每次取都现拼。调用方只有一处（统计窗口列历史里已经不在名单上的人），
    /// 而名单是几十人的量级，现拼比长期维护第二份数据结构更省心，也不会不同步。
    /// </remarks>
    public IReadOnlyDictionary<string, int> Counts
    {
        get
        {
            var map = new Dictionary<string, int>(_counts.Count, StringComparer.Ordinal);
            foreach (var pick in _counts)
            {
                map[pick.Name] = pick.Count;
            }

            return map;
        }
    }

    /// <summary>这份存档是不是还要从 settings.json 补一次本轮进度。宿主读完文件后据此决定要不要补。</summary>
    public bool NeedsSettingsAdoption => !_baselineReady;

    #region 查询

    private Entry? Find(string name)
    {
        foreach (var pick in _counts)
        {
            if (string.Equals(pick.Name, name, StringComparison.Ordinal))
            {
                return pick;
            }
        }

        return null;
    }

    /// <summary>某人至今被抽到过多少次。没记录就是 0。</summary>
    public int CountOf(string name) => Find(name)?.Count ?? 0;

    /// <summary>还有没有可以撤回的抽签。</summary>
    public bool CanUndo => _history.Any(x => x.Kind == HistoryEntry.KindPick);

    /// <summary>逐次历史里现在有多少条（不含「开始新一轮」标记）。</summary>
    public int HistoryCount => _history.Count(x => x.Kind == HistoryEntry.KindPick);

    /// <summary>最近一次抽中。没有历史时是 <c>null</c>。</summary>
    public PickRecord? LastPick
    {
        get
        {
            for (var i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].Kind == HistoryEntry.KindPick)
                {
                    return ToRecord(_history[i]);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 取最近的抽签历史，<b>最新的排在前面</b>。
    /// </summary>
    /// <param name="max">最多取几条。</param>
    public IReadOnlyList<PickRecord> History(int max = int.MaxValue)
    {
        var list = new List<PickRecord>(Math.Min(max, _history.Count));

        for (var i = _history.Count - 1; i >= 0 && list.Count < max; i--)
        {
            if (_history[i].Kind == HistoryEntry.KindPick)
            {
                list.Add(ToRecord(_history[i]));
            }
        }

        return list;
    }

    /// <summary>
    /// 名单里被抽到过的最少 / 最多次数。
    /// </summary>
    /// <remarks>
    /// 一次遍历同时取出两端，调用方各取所需——统计摘要把两个数并排显示，
    /// 分两次遍历纯属白跑。名单为空时两端都是 0。
    /// </remarks>
    private (int Min, int Max) CountRange(IEnumerable<string>? names)
    {
        var min = int.MaxValue;
        var max = int.MinValue;

        if (names is not null)
        {
            foreach (var name in names)
            {
                var count = CountOf(name);
                if (count < min)
                {
                    min = count;
                }

                if (count > max)
                {
                    max = count;
                }
            }
        }

        return min == int.MaxValue ? (0, 0) : (min, max);
    }

    /// <summary>名单里被抽到过的最少次数。名单为空时返回 0。</summary>
    public int MinCount(IEnumerable<string> names) => CountRange(names).Min;

    /// <summary>名单里被抽到过的最多次数。名单为空时返回 0。</summary>
    public int MaxCount(IEnumerable<string> names) => CountRange(names).Max;

    /// <summary>
    /// 拼出给统计窗口用的表格数据。
    /// </summary>
    /// <param name="roster">当前名单。</param>
    /// <param name="drawnThisRound">本轮已经抽到过的人。</param>
    /// <remarks>
    /// 排序规则是<b>次数多的在前、同次数按姓名</b>，保证同一个状态每次打开顺序都一样，
    /// 不会看着看着跳位置。历史里还留着、但已经从名单删掉的人也会带出来（标成「已移出名单」），
    /// 否则用户会以为记录被吞了。
    /// </remarks>
    public List<PickStatsRow> BuildRows(IEnumerable<string> roster,
        IReadOnlyCollection<string>? drawnThisRound = null)
    {
        var drawn = drawnThisRound is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(drawnThisRound, StringComparer.Ordinal);

        var rows = new List<PickStatsRow>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var divisor = Math.Max(1, TotalPicks);

        void Add(string name, bool inRoster)
        {
            if (!listed.Add(name))
            {
                return;
            }

            var count = CountOf(name);
            rows.Add(new PickStatsRow(name, count, inRoster, drawn.Contains(name), count / (double)divisor));
        }

        foreach (var name in roster)
        {
            Add(name, true);
        }

        foreach (var pick in _counts)
        {
            Add(pick.Name, false);
        }

        // 次数降序；次数相同时按姓名升序，让顺序完全确定。
        rows.Sort((left, right) =>
        {
            var byCount = right.Count.CompareTo(left.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(left.Name, right.Name);
        });

        return rows;
    }

    #endregion

    #region 变更

    /// <summary>记一次「抽到了某人」。每次真的抽出一个名字都要走这儿。</summary>
    /// <param name="name">抽中的人。</param>
    /// <param name="startedNewRound">这一抽是不是把上一轮抽满后自动开启了新一轮。</param>
    /// <param name="rosterSize">抽这一抽时名单上有多少人，只用于历史详情展示。</param>
    /// <returns>刚记下的那一条。</returns>
    public PickRecord Record(string name, bool startedNewRound = false, int rosterSize = 0)
    {
        EnsureBaseline();

        var item = new HistoryEntry
        {
            Id = NextId(),
            Kind = HistoryEntry.KindPick,
            Name = name,
            At = DateTimeOffset.Now,
            StartedNewRound = startedNewRound,
            RosterSize = rosterSize
        };

        _history.Add(item);
        FoldOverflow();
        Recompute();
        _updated = DateTimeOffset.Now;
        return ToRecord(item);
    }

    /// <summary>记一次「手动开始新一轮」。它不改变次数，只把重放时的轮次推进一格。</summary>
    public void BeginRound()
    {
        EnsureBaseline();

        _history.Add(new HistoryEntry
        {
            Id = NextId(),
            Kind = HistoryEntry.KindRound,
            At = DateTimeOffset.Now
        });

        FoldOverflow();
        Recompute();
        _updated = DateTimeOffset.Now;
    }

    /// <summary>
    /// 撤回指定的几条抽签记录。
    /// </summary>
    /// <param name="ids">要撤回的 <see cref="PickRecord.Id"/>。可以是最新一抽，也可以是中间任意几条。</param>
    /// <returns>真正被撤掉的记录（按时间从旧到新）；没有一条命中时是空表。</returns>
    /// <remarks>
    /// <b>删掉之后整段历史重放一遍</b>，所以撤中间那一条不会留下「后面几抽的轮次和本轮进度错位」
    /// 这种半截状态：如果撤掉的正好是某一轮的第一抽，那一轮就并回上一轮，轮次数少一轮；
    /// 本轮进度按「重放后谁已经抽过」重新算，被撤的人自然回到待抽。
    /// </remarks>
    public IReadOnlyList<PickRecord> Undo(IEnumerable<string> ids)
    {
        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return [];
        }

        var removed = new List<PickRecord>();
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            var item = _history[i];
            if (item.Kind == HistoryEntry.KindPick && wanted.Contains(item.Id))
            {
                removed.Add(ToRecord(item));
                _history.RemoveAt(i);
            }
        }

        if (removed.Count == 0)
        {
            return [];
        }

        removed.Reverse();
        Recompute();
        _updated = DateTimeOffset.Now;
        return removed;
    }

    /// <summary>撤回最近一次抽中。</summary>
    /// <returns>被撤回的那一条；没有历史时返回 <c>null</c>。</returns>
    public PickRecord? UndoLast()
    {
        var last = LastPick;
        return last is null ? null : Undo([last.Id]).FirstOrDefault();
    }

    /// <summary>把历史清空，从零开始重新统计。</summary>
    public void ResetAll()
    {
        _baseline = new Baseline();
        _baselineReady = true;
        _settingsAdopted = true;

        _history.Clear();
        _counts.Clear();
        _drawn.Clear();
        _recent.Clear();
        _nextId = 1;
        _round = 1;
        _lastPicked = null;
        _updated = DateTimeOffset.Now;
    }

    /// <summary>
    /// 把重放出来的状态写回设置对象。
    /// </summary>
    /// <remarks>
    /// 「本轮已抽」「上次抽中」「最近抽过」在设置里本来就有字段（老版本就靠它们），
    /// 现在它们只是历史的一个视图：每次抽、每次撤之后都由这里统一同步，
    /// 避免出现「统计文件说撤了、设置文件还记着」的两份真相。
    /// </remarks>
    public void ApplyTo(PickSettings settings)
    {
        var drawn = settings.DrawnThisRound ??= [];
        drawn.Clear();
        foreach (var name in _drawn)
        {
            if (!drawn.Contains(name, StringComparer.Ordinal))
            {
                drawn.Add(name);
            }
        }

        settings.LastPicked = _lastPicked;

        var recent = settings.RecentPicks ??= [];
        recent.Clear();
        recent.AddRange(_recent);
    }

    /// <summary>
    /// 从 settings.json 补一次设置状态。
    /// </summary>
    /// <param name="drawnThisRound">本轮已经抽到过的人。</param>
    /// <param name="lastPicked">上一次抽中的人。</param>
    /// <param name="recentPicks">最近抽过的人（最近的在前）。</param>
    /// <remarks>
    /// 只有 3 版及以前、或者全新安装才需要调；4 版存档自己带着起点，调了也不会重复扣。
    /// </remarks>
    public void AdoptSettingsState(IReadOnlyList<string>? drawnThisRound, string? lastPicked,
        IReadOnlyList<string>? recentPicks)
    {
        if (_settingsAdopted)
        {
            return;
        }

        EnsureBaseline();

        _baseline.DrawnThisRound = [];
        if (drawnThisRound is not null)
        {
            // 历史里已经记着的那几个人重放时自然会回到「本轮已抽」，
            // 这里只留历史开始之前就已经抽掉的部分。
            var inHistory = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in _history)
            {
                if (item.Kind == HistoryEntry.KindPick && item.StartedNewRound)
                {
                    inHistory.Clear();
                }

                if (item.Kind == HistoryEntry.KindPick)
                {
                    inHistory.Add(item.Name);
                }
            }

            foreach (var name in drawnThisRound)
            {
                if (!string.IsNullOrWhiteSpace(name) && !inHistory.Contains(name))
                {
                    _baseline.DrawnThisRound.Add(name);
                }
            }
        }

        _baseline.LastPicked = lastPicked;

        _baseline.RecentPicks = [];
        if (recentPicks is not null)
        {
            foreach (var name in recentPicks)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _baseline.RecentPicks.Add(name);
                }
            }
        }

        _settingsAdopted = true;
        _baselineReady = true;
        Recompute();
    }

    #endregion

    #region 重放

    /// <summary>
    /// 把「起点 + 历史」重新跑一遍，算出次数、轮次、本轮已抽、上次抽中、最近抽过。
    /// </summary>
    /// <remarks>
    /// <b>这是整个类的核心。</b>所有派生状态都只在这里产生，别处不再单独增减——
    /// 一处增量维护就多一处可能对不上的地方，撤回任意一条时也更容易出错。
    /// </remarks>
    private void Recompute()
    {
        _counts.Clear();
        foreach (var entry in _baseline.Counts)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name) && entry.Count > 0)
            {
                _counts.Add(new Entry { Name = entry.Name, Count = entry.Count });
            }
        }

        _round = Math.Max(1, _baseline.Round);

        _drawn.Clear();
        foreach (var name in _baseline.DrawnThisRound)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _drawn.Add(name);
            }
        }

        _lastPicked = _baseline.LastPicked;

        _recent.Clear();
        foreach (var name in _baseline.RecentPicks)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _recent.Add(name);
            }
        }

        foreach (var item in _history)
        {
            if (item.Kind == HistoryEntry.KindRound)
            {
                _round++;
                _drawn.Clear();
                continue;
            }

            if (item.StartedNewRound)
            {
                _round++;
                _drawn.Clear();
            }

            item.Round = _round;

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            AddCount(item.Name, 1);

            if (!_drawn.Contains(item.Name, StringComparer.Ordinal))
            {
                _drawn.Add(item.Name);
            }

            _lastPicked = item.Name;
            TouchRecent(_recent, item.Name);
        }

        TrimRecent(_recent);
    }

    /// <summary>把最旧的若干条折算进起点，保证历史不超过上限时次数和轮次一点不丢。</summary>
    private void FoldOverflow()
    {
        var excess = _history.Count - HistoryCapacity;
        if (excess <= 0)
        {
            return;
        }

        for (var i = 0; i < excess; i++)
        {
            var item = _history[i];

            if (item.Kind == HistoryEntry.KindRound)
            {
                _baseline.Round++;
                _baseline.DrawnThisRound.Clear();
                continue;
            }

            if (item.StartedNewRound)
            {
                _baseline.Round++;
                _baseline.DrawnThisRound.Clear();
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            var entry = _baseline.Counts.FirstOrDefault(
                x => string.Equals(x.Name, item.Name, StringComparison.Ordinal));
            if (entry is null)
            {
                _baseline.Counts.Add(new Entry { Name = item.Name, Count = 1 });
            }
            else
            {
                entry.Count++;
            }

            if (!_baseline.DrawnThisRound.Contains(item.Name, StringComparer.Ordinal))
            {
                _baseline.DrawnThisRound.Add(item.Name);
            }

            _baseline.LastPicked = item.Name;
            TouchRecent(_baseline.RecentPicks, item.Name);
        }

        TrimRecent(_baseline.RecentPicks);
        _history.RemoveRange(0, excess);
    }

    private void AddCount(string name, int delta)
    {
        var entry = Find(name);
        if (entry is null)
        {
            _counts.Add(new Entry { Name = name, Count = delta });
        }
        else
        {
            entry.Count += delta;
        }
    }

    /// <summary>把名字挪到「最近抽过」的最前面，去重并保持长度上限。</summary>
    private static void TouchRecent(List<string> recent, string name)
    {
        for (var i = recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(recent[i], name, StringComparison.Ordinal))
            {
                recent.RemoveAt(i);
            }
        }

        recent.Insert(0, name);
    }

    private static void TrimRecent(List<string> recent)
    {
        var excess = recent.Count - RecentCapacity;
        if (excess > 0)
        {
            recent.RemoveRange(RecentCapacity, excess);
        }
    }

    private static PickRecord ToRecord(HistoryEntry item) => new(
        item.Id, item.Name, item.At, item.StartedNewRound, item.Round, item.RosterSize);

    private string NextId() => (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 没有起点时，从「文件里读到的当前状态」倒推出一个起点。
    /// </summary>
    /// <remarks>
    /// 只对 3 版及以前的文件有意义：那会儿次数是单独存的，历史只是附带的记录，
    /// 两者可能对不上（比如历史被截断过）。这里用「当前次数 － 历史累计」把差额归到起点上，
    /// 保证读进来之后总次数不变；本轮已抽 / 上次抽中 / 最近抽过稍后由设置补上。
    /// </remarks>
    private void EnsureBaseline()
    {
        if (_baselineReady)
        {
            return;
        }

        var baseline = new Baseline
        {
            Round = Math.Max(1, _round),
            LastPicked = _lastPicked
        };

        foreach (var entry in _counts)
        {
            baseline.Counts.Add(new Entry { Name = entry.Name, Count = entry.Count });
        }

        var recent = _recent.ToList();
        baseline.RecentPicks = recent;

        foreach (var item in _history)
        {
            if (item.Kind == HistoryEntry.KindRound)
            {
                baseline.Round = Math.Max(1, baseline.Round - 1);
                continue;
            }

            if (item.StartedNewRound)
            {
                baseline.Round = Math.Max(1, baseline.Round - 1);
            }

            var entry = baseline.Counts.FirstOrDefault(
                x => string.Equals(x.Name, item.Name, StringComparison.Ordinal));
            if (entry is not null)
            {
                entry.Count--;
                if (entry.Count <= 0)
                {
                    baseline.Counts.Remove(entry);
                }
            }
        }

        _baseline = baseline;
        _baselineReady = true;
    }

    #endregion

    #region 读写

    /// <summary>
    /// 从磁盘读历史记录。
    /// </summary>
    /// <remarks>
    /// <b>两种格式都认：</b>本版本的带版本号信封，以及早期直接把「姓名 → 次数」字典存下来的裸格式。
    /// 用户不该因为插件升级就丢掉一学期攒下来的记录。
    /// <para/>
    /// 文件真的坏了就用空记录重来，但<b>先留一份 <c>.bad</c> 备份</b>——
    /// 直接删掉的话，用户连「数据曾经存在过」都不知道。
    /// </remarks>
    public static PickStats Load(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new PickStats { _baselineReady = true };
            return fresh;
        }

        // 读不出来（被占用、没权限）不算文件坏，不留 .bad，静默从空开始。
        var raw = JsonStorage.TryReadAll(path);
        if (raw is null)
        {
            return new PickStats { _baselineReady = true };
        }

        try
        {
            return ParseSnapshot(raw) ?? ParseLegacy(raw) ?? new PickStats { _baselineReady = true };
        }
        catch (Exception)
        {
            JsonStorage.KeepBrokenCopy(path);
            return new PickStats { _baselineReady = true };
        }
    }

    /// <summary>本版本的带版本号信封。</summary>
    private static PickStats? ParseSnapshot(string raw)
    {
        // **先认字段再反序列化。** 直接 Deserialize<Snapshot> 对任何合法 JSON 都会成功
        // ——Picks 有默认值空表，不是 null，于是旧格式也会被"成功"解析成零条记录，
        // 上层那个 ?? ParseLegacy 永远轮不到，用户一升级历史就没了。
        // 所以这里必须确认真的存在 Picks 数组，没有就交还给旧格式解析器。
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("Picks", out var picks)
            || picks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var snapshot = document.RootElement.Deserialize<Snapshot>(JsonStorage.Options);
        if (snapshot is null)
        {
            return null;
        }

        var stats = new PickStats
        {
            _round = Math.Max(1, snapshot.Round)
        };
        var truncated = false;
        stats._updated = snapshot.Updated;
        stats._nextId = Math.Max(1, snapshot.NextId);

        foreach (var pick in snapshot.Picks)
        {
            // 没有名字、或者次数不是正数的条目直接丢掉：手工改过的文件里什么都可能有。
            if (!string.IsNullOrWhiteSpace(pick.Name) && pick.Count > 0)
            {
                stats._counts.Add(new Entry { Name = pick.Name, Count = pick.Count });
            }
        }

        if (snapshot.History is not null)
        {
            foreach (var item in snapshot.History)
            {
                var isRound = string.Equals(item.Kind, HistoryEntry.KindRound, StringComparison.Ordinal);
                if (!isRound && string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                var entry = new HistoryEntry
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? stats.NextId() : item.Id,
                    Kind = isRound ? HistoryEntry.KindRound : HistoryEntry.KindPick,
                    Name = item.Name,
                    At = item.At,
                    StartedNewRound = item.StartedNewRound,
                    Round = item.Round,
                    RosterSize = item.RosterSize
                };

                stats._history.Add(entry);
            }

            if (stats._history.Count > HistoryCapacity)
            {
                // 先截掉最旧的，再倒推起点——顺序反了会把截掉的部分在次数里扣两次。
                stats._history.RemoveRange(0, stats._history.Count - HistoryCapacity);
                stats.EnsureBaseline();
                truncated = true;
            }
        }

        // 4 版存档自带起点，直接以它为准重放；3 版没有，等宿主补设置状态后再重放。
        if (snapshot.Baseline is not null && !truncated)
        {
            stats._baseline = snapshot.Baseline;
            stats._baselineReady = true;
            stats._settingsAdopted = true;
            stats.Recompute();
        }

        // 编号补给：老文件里可能一个都没有，新版文件里也可能手工改过。
        foreach (var item in stats._history)
        {
            if (long.TryParse(item.Id, out var parsedId) && parsedId >= stats._nextId)
            {
                stats._nextId = parsedId + 1;
            }
        }

        return stats;
    }

    /// <summary>早期版本直接序列化对象本身留下的裸格式。</summary>
    private static PickStats? ParseLegacy(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("Counts", out var counts)
            || counts.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 裸格式没有历史，次数直接就是起点；本轮进度由宿主从设置里补。
        var stats = new PickStats();

        if (document.RootElement.TryGetProperty("Rounds", out var rounds)
            && rounds.TryGetInt32(out var round))
        {
            stats._round = Math.Max(1, round);
        }

        foreach (var property in counts.EnumerateObject())
        {
            if (property.Value.TryGetInt32(out var count) && count > 0)
            {
                stats._counts.Add(new Entry { Name = property.Name, Count = count });
            }
        }

        return stats;
    }

    /// <summary>把当前状态打成落盘用的信封。</summary>
    private Snapshot BuildSnapshot() => new()
    {
        Schema = CurrentSchema,
        Round = _round,
        Updated = _updated ?? DateTimeOffset.Now,
        Picks = [.. _counts],
        History = [.. _history],
        Baseline = _baselineReady ? _baseline : null,
        NextId = _nextId
    };

    /// <summary>
    /// 原子写盘。
    /// </summary>
    /// <remarks>
    /// 先写 <c>xxx.tmp</c> 再覆盖，这样任何时刻磁盘上的正式文件
    /// 要么是旧的完整内容、要么是新的完整内容，不会出现半截。
    /// </remarks>
    public void Save(string path) => JsonStorage.WriteAtomic(path, BuildSnapshot());

    #endregion
}
