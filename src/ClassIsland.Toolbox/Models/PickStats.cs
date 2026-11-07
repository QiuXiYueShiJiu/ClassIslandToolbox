// 教学助手 v1.3.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>抽签历史里的一条：谁、什么时候、这一抽有没有顺带开启新一轮。</summary>
/// <param name="Name">抽中的人。</param>
/// <param name="At">抽中的时间。</param>
/// <param name="StartedNewRound">这一抽是不是把上一轮抽满后自动开启新一轮的那一抽。</param>
public sealed record PickRecord(string Name, DateTimeOffset At, bool StartedNewRound);

/// <summary>
/// 撤回一抽之后交还给调用方的信息。
/// </summary>
/// <param name="Name">被撤回的人。</param>
/// <param name="StartedNewRound">被撤回的这一抽当时有没有触发自动翻轮。</param>
/// <param name="PreviousDrawnThisRound">
/// 如果触发了自动翻轮，这里是翻轮之前「本轮已抽」的名单——调用方要拿它把本轮进度原样还回去。
/// </param>
public sealed record PickUndoResult(
    string Name,
    bool StartedNewRound,
    IReadOnlyList<string>? PreviousDrawnThisRound);

/// <summary>
/// 抽取次数的历史记录 —— 公平性的唯一依据。
/// </summary>
/// <remarks>
/// <b>为什么单独存一份文件，而不是塞进 settings.json：</b>
/// 那些开关丢了重新点一下就行，这份记录丢了就再也算不出「谁被抽得多」。
/// 它落在插件配置目录的 <c>幸运抽签统计.json</c> 里，换名单、改设置、重启都不动它。
/// <para/>
/// <b>内部结构是一张只记录「被抽到过的人」的条目表</b>，没有出现过的名字根本不占位置。
/// 这样做的直接好处是：文件里存的就是全部有效信息，不像「姓名 → 次数」的字典那样
/// 需要额外维护一个总数——总数按需求和出来即可，也就不会出现「总和和明细对不上」的状态。
/// 名单是几十人量级，线性查找的开销可以忽略，换来的是没有两份可能互相矛盾的数据。
/// <para/>
/// 写盘先落临时文件再原子替换：抽人可能连着抽，正好在写的时候断电／被杀进程的话，
/// 直接覆写会把文件截成半截 JSON，历史就全没了。
/// </remarks>
public sealed class PickStats
{
    /// <summary>当前存档格式版本。读旧文件时靠它决定怎么解析。</summary>
    /// <remarks>
    /// 3：在次数表之外多记一份「逐次抽取」的历史，撤回误抽 / 乱抽要靠它。
    /// 2 及以前的文件照常能读，只是没有可撤回的历史。
    /// </remarks>
    private const int CurrentSchema = 3;

    /// <summary>
    /// 逐次历史最多留多少条。
    /// </summary>
    /// <remarks>
    /// 次数表是独立的，截掉旧事件不影响公平性，只是更早的抽签不能再撤回了。
    /// 一学期几十人、几十轮也就是几百条，这个上限足够覆盖到「课上刚抽错」的场景。
    /// </remarks>
    private const int HistoryCapacity = 500;

    /// <summary>一条记录：谁、被抽到过几次。</summary>
    private sealed class Entry
    {
        public string Name { get; set; } = "";

        public int Count { get; set; }
    }

    /// <summary>逐次历史的一条。比对外暴露的 <see cref="PickRecord"/> 多存一份翻轮前的进度。</summary>
    private sealed class HistoryEntry
    {
        public string Name { get; set; } = "";

        public DateTimeOffset At { get; set; }

        public bool StartedNewRound { get; set; }

        public List<string>? PreviousDrawnThisRound { get; set; }
    }

    /// <summary>落盘用的信封。字段名和内部结构解耦，改内部实现不必动存档格式。</summary>
    private sealed class Snapshot
    {
        public int Schema { get; set; } = CurrentSchema;

        public int Round { get; set; } = 1;

        public DateTimeOffset? Updated { get; set; }

        public List<Entry> Picks { get; set; } = [];

        /// <summary>逐次抽取历史，最旧的在前。2 版及以前的文件里没有这个字段。</summary>
        public List<HistoryEntry>? History { get; set; }
    }

    /// <summary>只装被抽到过的人。没有记录的人就是 0 次，不需要出现在这里。</summary>
    private readonly List<Entry> _picks = [];

    /// <summary>逐次抽取历史，最旧的在前。撤回只从末尾往回走。</summary>
    private readonly List<HistoryEntry> _history = [];

    private int _round = 1;
    private DateTimeOffset? _updated;

    /// <summary>当前进行到第几轮。小于 1 的值一律按 1 处理。</summary>
    public int Rounds
    {
        get => _round;
        set => _round = value < 1 ? 1 : value;
    }

    /// <summary>累计抽了多少次。按明细求和，不单独存——省掉一处可能对不上的冗余。</summary>
    public int TotalPicks
    {
        get
        {
            var sum = 0;
            foreach (var pick in _picks)
            {
                sum += pick.Count;
            }

            return sum;
        }
    }

    /// <summary>最后一次改动的时间，纯为了一眼看出数据新旧。</summary>
    public DateTimeOffset? UpdatedAt => _updated;

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
            var map = new Dictionary<string, int>(_picks.Count, StringComparer.Ordinal);
            foreach (var pick in _picks)
            {
                map[pick.Name] = pick.Count;
            }

            return map;
        }
    }

    #region 查询

    private Entry? Find(string name)
    {
        foreach (var pick in _picks)
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
    public bool CanUndo => _history.Count > 0;

    /// <summary>逐次历史里现在有多少条。</summary>
    public int HistoryCount => _history.Count;

    /// <summary>最近一次抽中。没有历史时是 <c>null</c>。</summary>
    public PickRecord? LastPick => _history.Count == 0
        ? null
        : new PickRecord(_history[^1].Name, _history[^1].At, _history[^1].StartedNewRound);

    /// <summary>
    /// 取最近的抽签历史，<b>最新的排在前面</b>。
    /// </summary>
    /// <param name="max">最多取几条。</param>
    public IReadOnlyList<PickRecord> History(int max = int.MaxValue)
    {
        var take = Math.Min(max, _history.Count);
        var list = new List<PickRecord>(take);

        for (var i = _history.Count - 1; i >= 0 && list.Count < take; i--)
        {
            list.Add(new PickRecord(_history[i].Name, _history[i].At, _history[i].StartedNewRound));
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

        foreach (var pick in _picks)
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
    /// <param name="previousDrawnThisRound">
    /// 如果自动翻了轮，传翻轮之前「本轮已抽」的名单，撤回时靠它原样还回去。
    /// </param>
    public void Record(string name, bool startedNewRound = false,
        IReadOnlyList<string>? previousDrawnThisRound = null)
    {
        var entry = Find(name);
        if (entry is null)
        {
            _picks.Add(new Entry { Name = name, Count = 1 });
        }
        else
        {
            entry.Count++;
        }

        _history.Add(new HistoryEntry
        {
            Name = name,
            At = DateTimeOffset.Now,
            StartedNewRound = startedNewRound,
            PreviousDrawnThisRound = previousDrawnThisRound?.ToList()
        });

        if (_history.Count > HistoryCapacity)
        {
            _history.RemoveRange(0, _history.Count - HistoryCapacity);
        }

        _updated = DateTimeOffset.Now;
    }

    /// <summary>
    /// 撤回最近一次抽中：次数减一、历史去掉末尾一条；如果那一抽顺带翻了轮，轮次也退回去。
    /// </summary>
    /// <returns>撤回的信息；没有历史时返回 <c>null</c>。</returns>
    /// <remarks>
    /// <b>只从末尾往回退，不支持抽掉中间一条。</b>中间一条一撤，后面几抽的「本轮已抽」
    /// 和轮次就全乱了；要撤到更早的位置，连点几次「撤回上一抽」即可，行为完全可预期。
    /// </remarks>
    public PickUndoResult? UndoLast()
    {
        if (_history.Count == 0)
        {
            return null;
        }

        var last = _history[^1];
        _history.RemoveAt(_history.Count - 1);

        var entry = Find(last.Name);
        if (entry is not null)
        {
            entry.Count--;
            if (entry.Count <= 0)
            {
                _picks.Remove(entry);
            }
        }

        // 翻轮是这一抽引起的，撤了它自然要退回上一轮。Rounds 的 setter 兜底不会小于 1。
        if (last.StartedNewRound)
        {
            Rounds--;
        }

        _updated = DateTimeOffset.Now;
        return new PickUndoResult(last.Name, last.StartedNewRound, last.PreviousDrawnThisRound);
    }

    /// <summary>一轮抽完了，进入下一轮。</summary>
    public void StartNewRound()
    {
        Rounds++;
        _updated = DateTimeOffset.Now;
    }

    /// <summary>把历史清空，从零开始重新统计。</summary>
    public void ResetAll()
    {
        _picks.Clear();
        _history.Clear();
        _round = 1;
        _updated = DateTimeOffset.Now;
    }

    #endregion

    #region 读写


    /// <summary>
    /// 从磁盘读历史记录。
    /// </summary>
    /// <remarks>
    /// <b>两种格式都认：</b>本版本的信封格式，以及早期直接把「姓名 → 次数」字典存下来的裸格式。
    /// 用户不该因为插件升级就丢掉一学期攒下来的记录。
    /// <para/>
    /// 文件真的坏了就用空记录重来，但<b>先留一份 <c>.bad</c> 备份</b>——
    /// 直接删掉的话，用户连「数据曾经存在过」都不知道。
    /// </remarks>
    public static PickStats Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PickStats();
        }

        // 读不出来（被占用、没权限）不算文件坏，不留 .bad，静默从空开始。
        var raw = JsonStorage.TryReadAll(path);
        if (raw is null)
        {
            return new PickStats();
        }

        try
        {
            return ParseSnapshot(raw) ?? ParseLegacy(raw) ?? new PickStats();
        }
        catch (Exception)
        {
            JsonStorage.KeepBrokenCopy(path);
            return new PickStats();
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

        var stats = new PickStats { Rounds = snapshot.Round };
        stats._updated = snapshot.Updated;

        foreach (var pick in snapshot.Picks)
        {
            // 没有名字、或者次数不是正数的条目直接丢掉：手工改过的文件里什么都可能有。
            if (!string.IsNullOrWhiteSpace(pick.Name) && pick.Count > 0)
            {
                stats._picks.Add(new Entry { Name = pick.Name, Count = pick.Count });
            }
        }

        // 逐次历史是 3 版才有的，旧文件里就是 null——读不到就当没有可撤回的记录。
        if (snapshot.History is not null)
        {
            foreach (var item in snapshot.History)
            {
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                stats._history.Add(new HistoryEntry
                {
                    Name = item.Name,
                    At = item.At,
                    StartedNewRound = item.StartedNewRound,
                    PreviousDrawnThisRound = item.PreviousDrawnThisRound
                });
            }

            if (stats._history.Count > HistoryCapacity)
            {
                stats._history.RemoveRange(0, stats._history.Count - HistoryCapacity);
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

        var stats = new PickStats();

        if (document.RootElement.TryGetProperty("Rounds", out var rounds)
            && rounds.TryGetInt32(out var round))
        {
            stats.Rounds = round;
        }

        foreach (var property in counts.EnumerateObject())
        {
            if (property.Value.TryGetInt32(out var count) && count > 0)
            {
                stats._picks.Add(new Entry { Name = property.Name, Count = count });
            }
        }

        return stats;
    }


    /// <summary>
    /// 原子写盘。
    /// </summary>
    /// <remarks>
    /// 先写 <c>xxx.tmp</c> 再覆盖，这样任何时刻磁盘上的正式文件
    /// 要么是旧的完整内容、要么是新的完整内容，不会出现半截。
    /// </remarks>
    /// <summary>把当前状态打成落盘用的信封。</summary>
    private Snapshot BuildSnapshot() => new()
    {
        Schema = CurrentSchema,
        Round = Rounds,
        Updated = _updated ?? DateTimeOffset.Now,
        Picks = [.. _picks],
        History = [.. _history]
    };

    public void Save(string path) => JsonStorage.WriteAtomic(path, BuildSnapshot());

    #endregion
}
