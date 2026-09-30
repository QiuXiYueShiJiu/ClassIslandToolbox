// 教学助手 v1.0.0：ClassIsland 置顶工具条插件
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using ClassIsland.Toolbox.Services;

namespace ClassIsland.Toolbox.Models;

/// <summary>
/// 一条自定义快捷方式的类型。
/// </summary>
public enum ShortcutKind
{
    /// <summary>启动一个程序或执行一条命令。</summary>
    Command,

    /// <summary>用默认浏览器打开一个网址。</summary>
    Url,

    /// <summary>用系统默认程序打开一个文件或文件夹。</summary>
    Path,

    /// <summary>在 ClassIsland 内部导航到某个 <c>classisland://</c> 地址。</summary>
    ClassIslandUri
}

/// <summary>
/// 「打开文件或文件夹」这条快捷方式的目标怎么定位。
/// </summary>
/// <remarks>
/// 只对 <see cref="ShortcutKind.Path"/> 有意义，其它类型忽略这个字段。
/// 解析规则见 <c>ShortcutPathResolver</c>。
/// </remarks>
public enum PathMode
{
    /// <summary>直接就是绝对路径，例如 <c>D:\课件\第一课.pptx</c>。</summary>
    Absolute,

    /// <summary>相对于 ClassIsland 应用数据根目录，例如 <c>课件\第一课.pptx</c>。</summary>
    RelativeToApp,

    /// <summary>文件已经复制进插件内部目录，这里存的是副本文件名。</summary>
    InternalCopy
}

/// <summary>
/// 工具条上的一条自定义快捷方式。
/// </summary>
/// <remarks>
/// 实现 <see cref="INotifyPropertyChanged"/> 是为了设置页里能双向绑定——
/// 用户在 TextBox 里改一个字，旁边的预览和工具条立刻跟着变。
/// <para/>
/// <b>设置页里的表单是「按类型长」的</b>：启动程序有「浏览 exe」，
/// 打开文件/文件夹有「选文件 / 选文件夹」，ClassIsland 地址有常用地址下拉。
/// 靠 <see cref="IsCommand"/> / <see cref="IsUrl"/> / <see cref="IsPath"/> / <see cref="IsUri"/>
/// 这几个布尔属性控制各行的显隐，所以它们必须在 <see cref="Kind"/> 变化时一起通知。
/// </remarks>
public class ShortcutItem : INotifyPropertyChanged
{
    private string _name = "新快捷方式";
    private string _icon = "";
    private ShortcutKind _kind = ShortcutKind.Url;
    private PathMode _pathMode = PathMode.Absolute;
    private string _target = "";
    private string _arguments = "";
    private bool _useFileIcon = true;
    private bool _runAsAdmin;
    private bool _confirm;

    /// <summary>
    /// 稳定标识。
    /// </summary>
    /// <remarks>
    /// 工具条的顺序是按 id 记的（见 <see cref="ToolboxSettings.ItemOrder"/>），
    /// 而不是按下标——下标在拖动排序时一直在变，记不住。
    /// 用户看不到它，也不需要管它。
    /// </remarks>
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

    /// <summary>按钮上显示的名称。</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>
    /// 按钮上显示的图标，一般填一个 emoji。
    /// </summary>
    /// <remarks>
    /// 留空就按类型自动给一个（见 <see cref="IconOrDefault"/>）。
    /// 用 emoji 而不是 Fluent 图标字形，是因为字形要靠宿主那边的字体，
    /// 换到 Linux / macOS 上就是一个方框；emoji 各平台都有。
    /// </remarks>
    public string Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    /// <summary>类型。</summary>
    public ShortcutKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value))
            {
                return;
            }

            // 换了类型，设置页里那一整组表单都要跟着换；按钮上的默认图标也可能变。
            foreach (var name in new[]
                     {
                         nameof(IsCommand), nameof(IsUrl), nameof(IsPath), nameof(IsUri),
                         nameof(IsFileBased),
                         nameof(IsPathAbsolute), nameof(IsPathRelative), nameof(IsPathInternal),
                         nameof(ShowResolvedTarget), nameof(ResolvedTarget),
                         nameof(KindIndex), nameof(IconOrDefault), nameof(TargetWatermark)
                     })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    /// <summary>
    /// 「打开文件或文件夹」的目标怎么定位（绝对 / 相对 CI / 插件内部副本）。
    /// </summary>
    /// <remarks>
    /// 只有 <see cref="ShortcutKind.Path"/> 用得上。换模式时目标框的提示文字、
    /// 「实际打开」那一行、以及下面几个按钮的显隐都要跟着变，所以这里一并通知。
    /// </remarks>
    public PathMode PathMode
    {
        get => _pathMode;
        set
        {
            if (!Set(ref _pathMode, value))
            {
                return;
            }

            foreach (var name in new[]
                     {
                         nameof(IsPathAbsolute), nameof(IsPathRelative), nameof(IsPathInternal),
                         nameof(ShowResolvedTarget), nameof(ResolvedTarget),
                         nameof(PathModeIndex), nameof(PathModeHint), nameof(TargetWatermark)
                     })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    /// <summary>目标：程序路径、网址、文件路径或 <c>classisland://</c> 地址。</summary>
    public string Target
    {
        get => _target;
        set => Set(ref _target, value);
    }

    /// <summary>命令行参数，只有 <see cref="ShortcutKind.Command"/> 用得上。</summary>
    public string Arguments
    {
        get => _arguments;
        set => Set(ref _arguments, value);
    }

    /// <summary>
    /// 用<strong>文件本身的图标</strong>当按钮图标。
    /// </summary>
    /// <remarks>
    /// 只对「启动程序 / 命令」和「打开文件或文件夹」有意义——
    /// 它们是取系统资源管理器里显示的那个图标（走 <c>SHGetFileInfo</c>）。
    /// 取不到（文件不在、或者当前不是 Windows）就退回 <see cref="IconOrDefault"/>。
    /// <para/>
    /// 网址和 ClassIsland 地址没有文件可查，这个开关对它们无效。
    /// </remarks>
    public bool UseFileIcon
    {
        get => _useFileIcon;
        set => Set(ref _useFileIcon, value);
    }

    /// <summary>以管理员身份运行，只有 <see cref="ShortcutKind.Command"/> 用得上。</summary>
    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set => Set(ref _runAsAdmin, value);
    }

    /// <summary>
    /// 点第一次先「上膛」，再点一次才真的执行。
    /// </summary>
    /// <remarks>
    /// 悬浮工具条常驻在屏幕上，很容易误触。像「关机」「重启」这种快捷方式勾上这个会安全很多。
    /// 第一次点击按钮会变红并显示「再点一次」，几秒内没点第二次就自己取消。
    /// </remarks>
    public bool Confirm
    {
        get => _confirm;
        set => Set(ref _confirm, value);
    }

    #region 只读派生属性（给界面用，不进配置文件）

    [JsonIgnore]
    public bool IsCommand => Kind == ShortcutKind.Command;

    [JsonIgnore]
    public bool IsUrl => Kind == ShortcutKind.Url;

    [JsonIgnore]
    public bool IsPath => Kind == ShortcutKind.Path;

    [JsonIgnore]
    public bool IsUri => Kind == ShortcutKind.ClassIslandUri;

    /// <summary>这一条指向一个真实的文件/文件夹（能查系统图标、能用文件选择框）。</summary>
    [JsonIgnore]
    public bool IsFileBased => Kind is ShortcutKind.Command or ShortcutKind.Path;

    /// <summary>「打开文件或文件夹」+ 绝对路径。</summary>
    [JsonIgnore]
    public bool IsPathAbsolute => Kind == ShortcutKind.Path && PathMode == PathMode.Absolute;

    /// <summary>「打开文件或文件夹」+ 相对 ClassIsland 数据根目录的路径。</summary>
    [JsonIgnore]
    public bool IsPathRelative => Kind == ShortcutKind.Path && PathMode == PathMode.RelativeToApp;

    /// <summary>「打开文件或文件夹」+ 指向插件内部保存的副本。</summary>
    [JsonIgnore]
    public bool IsPathInternal => Kind == ShortcutKind.Path && PathMode == PathMode.InternalCopy;

    /// <summary>目标最终解析成的绝对路径，设置页上摊开给用户核对。</summary>
    [JsonIgnore]
    public string ResolvedTarget => ShortcutPathResolver.Resolve(this);

    /// <summary>要不要显示「实际打开」那一行。绝对路径下它和目标一模一样，没必要重复。</summary>
    [JsonIgnore]
    public bool ShowResolvedTarget => Kind == ShortcutKind.Path && PathMode != PathMode.Absolute;

    /// <summary>路径方式下拉框的中文名，和 <see cref="PathMode"/> 一一对应。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> PathModeNames { get; } =
        ["绝对路径", "相对路径（CI 内）", "插件内部副本"];

    [JsonIgnore]
    public IReadOnlyList<string> PathModeOptions => PathModeNames;

    /// <summary>给 <c>ComboBox.SelectedIndex</c> 用的整数包装。</summary>
    [JsonIgnore]
    public int PathModeIndex
    {
        get => (int)PathMode;
        set
        {
            if (value < 0 || value > (int)PathMode.InternalCopy)
            {
                return;
            }

            PathMode = (PathMode)value;
        }
    }

    /// <summary>当前路径方式的短说明（具体基准目录由设置页统一显示一次，不在这里逐条重复）。</summary>
    [JsonIgnore]
    public string PathModeHint => PathMode switch
    {
        PathMode.Absolute => "填完整的绝对路径，换机器/换盘符后需要重新改。",
        PathMode.RelativeToApp => "填相对于 ClassIsland 应用数据根目录的路径，整个目录搬走也还能找到。",
        PathMode.InternalCopy => "文件已复制进插件内部目录，跟着插件配置走，原文件放哪都不影响。",
        _ => ""
    };

    /// <summary>按钮上真正显示的图标：没填就按类型给一个默认的。</summary>
    [JsonIgnore]
    public string IconOrDefault =>
        string.IsNullOrWhiteSpace(Icon) ? DefaultIconFor(Kind) : Icon.Trim();

    /// <summary>按类型给默认图标。</summary>
    public static string DefaultIconFor(ShortcutKind kind) => kind switch
    {
        ShortcutKind.Command => "🖥",
        ShortcutKind.Url => "🌐",
        ShortcutKind.Path => "📁",
        ShortcutKind.ClassIslandUri => "🏝",
        _ => "🔗"
    };

    /// <summary>类型下拉框的中文名，和枚举一一对应。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> KindNames { get; } =
        ["启动程序 / 命令", "打开网址", "打开文件或文件夹", "ClassIsland 地址"];

    [JsonIgnore]
    public IReadOnlyList<string> KindOptions => KindNames;

    /// <summary>可选图标的快捷列表。不是所有人都方便用输入法打 emoji。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> IconChoices { get; } =
        ["🖥", "🌐", "📁", "📄", "📝", "🧮", "✂", "📷", "🎵", "🎬", "📊", "🔔", "⚙", "⭐", "🔁", "🏝"];

    [JsonIgnore]
    public IReadOnlyList<string> IconOptions => IconChoices;

    /// <summary>ClassIsland 常用地址。只列确定存在的几个，写错了不会报错、只会没反应。</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> UriValues { get; } =
    [
        "classisland://app/settings",
        "classisland://app/profile",
        "classisland://app/edit",
        "classisland://app/test"
    ];

    [JsonIgnore]
    public static IReadOnlyList<string> UriNames { get; } =
        ["应用设置", "档案设置", "主界面编辑模式", "导航测试"];

    [JsonIgnore]
    public IReadOnlyList<string> UriOptions => UriNames;

    /// <summary>
    /// 「常用地址」下拉的选中项。
    /// </summary>
    /// <remarks>
    /// 取恒定返回 -1，所以它看起来永远是个空的下拉框——它的角色是「插入」而不是「选择」。
    /// 选中之后把地址写进 <see cref="Target"/>，再通知一次让自己弹回空白，方便连着插第二次。
    /// </remarks>
    [JsonIgnore]
    public int UriPresetIndex
    {
        get => -1;
        set
        {
            if (value >= 0 && value < UriValues.Count)
            {
                Target = UriValues[value];
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UriPresetIndex)));
        }
    }

    /// <summary>图标下拉的选中项，同样是「插入」语义。</summary>
    [JsonIgnore]
    public int IconPresetIndex
    {
        get => -1;
        set
        {
            if (value >= 0 && value < IconChoices.Count)
            {
                Icon = IconChoices[value];
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPresetIndex)));
        }
    }

    /// <summary>给 <c>ComboBox.SelectedIndex</c> 用的整数包装。</summary>
    [JsonIgnore]
    public int KindIndex
    {
        get => (int)Kind;
        set
        {
            if (value < 0 || value > (int)ShortcutKind.ClassIslandUri)
            {
                return;
            }

            Kind = (ShortcutKind)value;
        }
    }

    /// <summary>不同类型下「目标」输入框的提示文字。</summary>
    [JsonIgnore]
    public string TargetWatermark => Kind switch
    {
        ShortcutKind.Command => @"例如 notepad.exe 或 C:\Tools\x.exe",
        ShortcutKind.Url => "例如 https://www.classisland.tech",
        ShortcutKind.Path => PathMode switch
        {
            PathMode.RelativeToApp => @"例如 课件\第一课.pptx（相对 ClassIsland 数据根目录）",
            PathMode.InternalCopy => "副本文件名（点「保存副本…」会自动填好）",
            _ => @"例如 D:\课件 或 D:\课件\第一课.pptx"
        },
        ShortcutKind.ClassIslandUri => "例如 classisland://app/settings",
        _ => ""
    };

    /// <summary>悬浮钮上显示的文字，太长的名字截断，免得把工具条撑爆。</summary>
    [JsonIgnore]
    public string ButtonText => Name.Length <= 6 ? Name : Name[..6];

    #endregion

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>属性真的变了才发通知，返回是否变了。</summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Name))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonText)));
        }

        if (name == nameof(Icon))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconOrDefault)));
        }

        if (name == nameof(Target))
        {
            // 「实际打开」那一行显示的是解析后的路径，目标一改它就得跟着重画。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResolvedTarget)));
        }

        return true;
    }
}
