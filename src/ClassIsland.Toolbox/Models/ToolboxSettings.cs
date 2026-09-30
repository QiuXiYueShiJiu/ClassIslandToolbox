// 教学助手 v1.0.0：ClassIsland 置顶工具条插件
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassIsland.Toolbox.Models;

/// <summary>
/// 置顶工具集的全部设置。存在插件配置目录下的 <c>settings.json</c>。
/// </summary>
public class ToolboxSettings
{
    /// <summary>
    /// 是否启用幸运抽签。
    /// </summary>
    /// <remarks>
    /// 关掉之后工具条上不再出现「幸运抽签」，右键菜单里相关的几项也一并隐藏。
    /// 只想要一个纯快捷方式工具条的话可以关掉它，设置页里那一整块幸运抽签/拍照配置也会收起来。
    /// <para/>
    /// <b>不删除任何数据</b>：名单、幸运抽签统计都原样留着，重新打开就能接着用。
    /// </remarks>
    public bool EnableLuckyDraw { get; set; } = true;

    /// <summary>工具条上是否显示「批注」和「橡皮」（屏幕批注）。</summary>
    public bool EnableAnnotate { get; set; } = true;

    /// <summary>批注当前用的是哪支工具（软笔 / 荧光笔 / 橡皮 / 激光笔）。</summary>
    public AnnotateTool AnnotateTool { get; set; } = AnnotateTool.Pen;

    /// <summary>软笔和荧光笔的粗细档位（0 细 / 1 中 / 2 粗）。</summary>
    public int AnnotatePenSize { get; set; } = 1;

    /// <summary>橡皮档位（0 小 / 1 中 / 2 大）。</summary>
    public int AnnotateEraserSize { get; set; } = 1;

    /// <summary>软笔的颜色（<c>#RRGGBB</c>）。</summary>
    public string AnnotatePenColor { get; set; } = "#FF3B30";

    /// <summary>荧光笔的颜色（<c>#RRGGBB</c>）。</summary>
    public string AnnotateHighlightColor { get; set; } = "#FFEB3B";

    /// <summary>
    /// 悬浮窗的不透明度（0.3~1.0）。
    /// </summary>
    /// <remarks>
    /// 讲课时工具条压在课件上，有时候会挡住关键内容。调低一点能看穿过去，
    /// 又不像"隐藏"那样找不回来。
    /// </remarks>
    public double ToolbarOpacity { get; set; } = 1.0;

    /// <summary>允许的不透明度范围。</summary>
    public const double MinOpacity = 0.3;

    /// <inheritdoc cref="MinOpacity"/>
    public const double MaxOpacity = 1.0;

    /// <summary>
    /// 主按钮的直径（逻辑像素）。
    /// </summary>
    /// <remarks>
    /// <b>是一个连续值，不是三档。</b>原来是「小 / 中 / 大」三个枚举，
    /// 但教室里的屏幕尺寸千差万别，三档很难正好合适；改成数值之后设置页上可以用滑动条细调，
    /// 右键菜单里仍然留了三个常用档位做快捷键。
    /// <para/>
    /// 其它所有尺寸都是从这里推出来的：按钮高度、大字字号、人像高度，
    /// 所以调一个数整条就按比例缩放。
    /// </remarks>
    public double Diameter { get; set; } = DefaultDiameter;

    /// <summary>默认直径。</summary>
    public const double DefaultDiameter = 68;

    /// <summary>允许的直径范围。</summary>
    public const double MinDiameter = 40;

    /// <inheritdoc cref="MinDiameter"/>
    public const double MaxDiameter = 140;

    /// <summary>悬浮钮位置（物理像素）。<see cref="int.MinValue"/> 表示还没摆过，用默认位置。</summary>
    public int WindowX { get; set; } = int.MinValue;

    public int WindowY { get; set; } = int.MinValue;

    /// <summary>中央大字停留秒数。</summary>
    public double RevealSeconds { get; set; } = 2.5;

    /// <summary>抽到人之后是否同时发一条 ClassIsland 提醒。</summary>
    public bool ShowNotification { get; set; } = true;

    /// <summary>内置「幸运抽签」在顺序表里的键。</summary>
    public const string LuckyDrawKey = "builtin:luckydraw";

    /// <summary>内置「批注」（软笔）在顺序表里的键。</summary>
    public const string AnnotateKey = "builtin:annotate";

    /// <summary>内置「橡皮」在顺序表里的键。</summary>
    public const string EraserKey = "builtin:eraser";

    /// <summary>
    /// 工具条上的排列顺序。
    /// </summary>
    /// <remarks>
    /// 每一项是一个键：内置功能用 <see cref="LuckyDrawKey"/> 这样的固定字符串，
    /// 自定义快捷方式用它的 <see cref="ShortcutItem.Id"/>。
    /// <b>用键而不是下标</b>：拖动排序时下标一直在变，记不住谁是谁。
    /// <para/>
    /// 这里只记「用户定过的顺序」；实际顺序由 <see cref="EffectiveOrder"/> 归一化出来——
    /// 删掉的、还没排进来的都会在那里补齐，所以手工改坏了也不会崩。
    /// </remarks>
    public List<string> ItemOrder { get; set; } = new();

    /// <summary>
    /// 归一化之后的实际排列顺序。
    /// </summary>
    /// <remarks>
    /// 规则：先按 <see cref="ItemOrder"/> 里记的排（认不出来的键直接丢掉），
    /// 再把还没排进去的补到末尾。这样「新加的快捷方式」「关掉又打开的幸运抽签」
    /// 都有确定的位置，不用去改顺序表。
    /// </remarks>
    public List<string> EffectiveOrder()
    {
        var available = new List<string>();
        if (EnableLuckyDraw)
        {
            available.Add(LuckyDrawKey);
        }

        if (EnableAnnotate)
        {
            // 批注和橡皮是**两颗独立的按钮**：想擦的时候不用先进菜单换工具。
            available.Add(AnnotateKey);
            available.Add(EraserKey);
        }

        available.AddRange(Shortcuts.Select(x => x.Id));

        var result = new List<string>();
        foreach (var key in ItemOrder)
        {
            if (available.Contains(key) && !result.Contains(key))
            {
                result.Add(key);
            }
        }

        foreach (var key in available)
        {
            if (!result.Contains(key))
            {
                result.Add(key);
            }
        }

        return result;
    }

    /// <summary>把「幸运抽签」这类内置功能挪到顺序表里指定的位置。</summary>
    public void SetOrder(IReadOnlyList<string> order)
    {
        ItemOrder = [.. order];
    }

    /// <summary>
    /// 用户自己加的快捷方式。幸运抽签是内置的，不在这张表里。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="ObservableCollection{T}"/> 而不是 List：设置页里增删要立刻反映到界面上，
    /// 用 List 的话 ItemsControl 不会知道集合变了。JSON 序列化对两者一视同仁。
    /// </remarks>
    public ObservableCollection<ShortcutItem> Shortcuts { get; set; } = new();

    /// <summary>幸运抽签相关的全部设置。</summary>
    public PickSettings Pick { get; set; } = new();

    #region 派生尺寸（不进配置文件）

    /// <summary>展开后每条快捷方式按钮的高度（逻辑像素）。比主按钮矮一圈，视觉上从属。</summary>
    /// <remarks>
    /// 0.76 不是随手取的：按钮纵向居中在「主按钮那么高」的一行里，
    /// 于是它正好一半落在下面的背景板上、一半悬空（背景板高度 = 直径的一半）。
    /// 改这个系数会同时改掉悬空比例。
    /// </remarks>
    [JsonIgnore]
    public double ItemHeight => Diameter * 0.76;

    /// <summary>中央大字的字号（逻辑像素）。</summary>
    [JsonIgnore]
    public double RevealFontSize => Diameter * 2.2;

    /// <summary>人像弹窗的高度（逻辑像素）。</summary>
    [JsonIgnore]
    public double PortraitHeight => Diameter * 7;

    #endregion

    #region 读写


    /// <summary>把外部留下的 null / 越界值修正回来。</summary>
    public void Normalize()
    {
        Shortcuts ??= new ObservableCollection<ShortcutItem>();
        ItemOrder ??= new List<string>();
        Pick ??= new PickSettings();
        Pick.Normalize();
        RevealSeconds = Math.Clamp(RevealSeconds, 0.5, 30);
        Diameter = Math.Clamp(Diameter, MinDiameter, MaxDiameter);
        ToolbarOpacity = Math.Clamp(ToolbarOpacity, MinOpacity, MaxOpacity);
        AnnotatePenSize = Math.Clamp(AnnotatePenSize, 0, AnnotationPalette.PenWidthFactors.Count - 1);
        AnnotateEraserSize = Math.Clamp(AnnotateEraserSize, 0, AnnotationPalette.EraserRadii.Count - 1);
        AnnotatePenColor = string.IsNullOrWhiteSpace(AnnotatePenColor) ? "#FF3B30" : AnnotatePenColor;
        AnnotateHighlightColor = string.IsNullOrWhiteSpace(AnnotateHighlightColor)
            ? "#FFEB3B"
            : AnnotateHighlightColor;

        for (var i = Shortcuts.Count - 1; i >= 0; i--)
        {
            if (Shortcuts[i] is null)
            {
                Shortcuts.RemoveAt(i);
                continue;
            }

            Shortcuts[i].Name ??= "";
            Shortcuts[i].Target ??= "";
            Shortcuts[i].Arguments ??= "";
            if (string.IsNullOrWhiteSpace(Shortcuts[i].Id))
            {
                // 老配置里没有 id：补一个，保证顺序表能对上号。
                Shortcuts[i].Id = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrWhiteSpace(Shortcuts[i].Name))
            {
                Shortcuts[i].Name = "未命名";
            }
        }
    }

    public static ToolboxSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<ToolboxSettings>(File.ReadAllText(path), JsonStorage.Options);
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // 配置坏了就用默认值重来。
        }

        return new ToolboxSettings();
    }

    /// <summary>
    /// 原子写盘：先写临时文件再替换，避免正好在写的时候被杀进程把配置截断。
    /// </summary>
    public void Save(string path) => JsonStorage.WriteAtomic(path, this);

    #endregion
}
