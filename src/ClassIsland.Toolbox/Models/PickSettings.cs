// 教学助手 v1.1.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System;
using System.Collections.Generic;

namespace ClassIsland.Toolbox.Models;

/// <summary>
/// 「幸运抽签」功能的全部设置。
/// </summary>
/// <remarks>
/// 抽选算法用的是<b>带次数历史的均衡抽选</b>：
/// 本轮抽过的人不再出现（一轮之内每人恰好一次），并且只在累计被抽次数最少的那一档人里抽，
/// 最后一步永远是在候选里等概率随机取一个（走操作系统熵源，不带种子）。
/// <para/>
/// <b>它不自己落盘</b>：作为 <see cref="ToolboxSettings"/> 的一部分写进 <c>settings.json</c>，
/// 所以属性名改了要小心老配置。
/// </remarks>
public class PickSettings
{
    /// <summary>名单模式下用的名单文件（相对插件配置目录的文件名）。</summary>
    public string RosterFileName { get; set; } = "名单.txt";

    /// <summary>本轮已经抽到过的人。</summary>
    public List<string> DrawnThisRound { get; set; } = new();

    /// <summary>上一次抽到的人。</summary>
    public string? LastPicked { get; set; }

    /// <summary>最近抽到过的人，最近的排在前面。每轮第一抽要靠它回避。</summary>
    public List<string> RecentPicks { get; set; } = new();

    /// <summary>每一轮的第一抽回避最近抽过的几个人（0~5，默认 1）。</summary>
    public int RoundStartAvoid { get; set; } = 1;

    /// <summary>把外部（老配置 / 手工改过的 json）留下的 null 和越界值修正回来。</summary>
    public void Normalize()
    {
        DrawnThisRound ??= new List<string>();
        RecentPicks ??= new List<string>();
        RoundStartAvoid = Math.Clamp(RoundStartAvoid, 0, 5);

        if (string.IsNullOrWhiteSpace(RosterFileName))
        {
            RosterFileName = "名单.txt";
        }
    }
}
