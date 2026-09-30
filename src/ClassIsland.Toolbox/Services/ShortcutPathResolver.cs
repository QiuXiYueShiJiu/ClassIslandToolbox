// 教学助手 v1.0.0：ClassIsland 置顶工具条插件
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ClassIsland.Toolbox.Models;

namespace ClassIsland.Toolbox.Services;

/// <summary>
/// 把「打开文件或文件夹」这条快捷方式的目标，解析成真正要打开的绝对路径。
/// </summary>
/// <remarks>
/// 三种模式（<see cref="PathMode"/>）解决的是三个不同的问题：
/// <list type="bullet">
/// <item><b>绝对路径</b>——最直白，但换机器、换盘符就废了；</item>
/// <item><b>相对路径（CI 内）</b>——存的是相对于 ClassIsland 应用数据根目录的路径，
/// 整包搬走之后依然能对上，适合「课件就放在 ClassIsland 目录里」的用法；</item>
/// <item><b>插件内部副本</b>——把文件<b>复制一份</b>到插件自己的数据目录里。
/// 原始文件在 U 盘上、在共享盘里、在别的老师电脑上都没关系，
/// 复制完就跟插件配置待在一起了。代价是占空间，所以只放开视频/图片/Office 这几类单文件。</item>
/// </list>
/// <para/>
/// <b>两个基准目录都用可覆盖的静态属性</b>，而不是每次都去问宿主：
/// 插件初始化时把内部目录注入进来（见 <c>ToolboxPlugin.Initialize</c>），
/// 无头测试直接改这两个属性就能把解析逻辑测穿，不需要真的跑起 ClassIsland。
/// </remarks>
public static class ShortcutPathResolver
{
    /// <summary>插件内部副本存放的子目录名（挂在插件配置目录下）。</summary>
    public const string InternalFolderName = "files";

    /// <summary>允许复制进插件内部的扩展名（全部小写，含点）。</summary>
    /// <remarks>
    /// 只收「单个文件」：视频、图片、Office/文档。
    /// 不收 exe / dll / 脚本——工具条是用来开课件的，不是用来分发程序的；
    /// 也不收文件夹——目录树复制进来之后没人知道什么时候该删。
    /// </remarks>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [
        // 视频
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".mpg", ".mpeg", ".ts", ".rmvb", ".rm", ".3gp", ".m2ts",
        // 图片
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".ico", ".svg", ".heic", ".avif",
        // Office / 文档
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".csv", ".pdf", ".rtf", ".odt", ".ods", ".odp"
    ];

    /// <summary>给文件选择框用的通配符。</summary>
    public static string[] PickerPatterns => [.. SupportedExtensions.Select(x => "*" + x)];

    /// <summary>相对路径的基准目录，测试用。</summary>
    public static string? AppRootOverride { get; set; }

    /// <summary>插件内部目录，测试用；运行时由插件初始化时注入。</summary>
    public static string? InternalRootOverride { get; set; }

    /// <summary>ClassIsland 应用数据根目录。取不到就返回空串，调用方按「无法解析」处理。</summary>
    public static string AppRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AppRootOverride))
            {
                return AppRootOverride!;
            }

            try
            {
                return ClassIsland.Core.CommonDirectories.AppRootFolderPath ?? "";
            }
            catch (Exception)
            {
                // 没跑在宿主里（比如无头测试）时它可能抛，安静退化成"没有基准"。
                return "";
            }
        }
    }

    /// <summary>插件内部副本目录（<c>&lt;插件配置目录&gt;/files</c>）。</summary>
    public static string InternalRoot
    {
        get => InternalRootOverride ?? "";
        set => InternalRootOverride = value;
    }

    /// <summary>插件初始化时调一次：把内部目录钉在插件自己的配置目录下。</summary>
    public static string EnsureInternalRoot(string pluginConfigFolder)
    {
        var path = Path.Combine(pluginConfigFolder, InternalFolderName);
        InternalRootOverride = path;
        return path;
    }

    /// <summary>这个文件能不能复制进插件内部（只看扩展名，不看存不存在）。</summary>
    public static bool IsSupportedInternalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext.ToLowerInvariant());
    }

    /// <summary>
    /// 解析出真正要打开的东西。
    /// </summary>
    /// <remarks>
    /// 只有 <see cref="ShortcutKind.Path"/> 走这套规则；
    /// 网址、程序、ClassIsland 地址都原样返回，免得把 <c>notepad.exe</c>
    /// 拼到 ClassIsland 目录底下去。
    /// </remarks>
    public static string Resolve(ShortcutItem? item)
    {
        if (item is null || item.Kind != ShortcutKind.Path)
        {
            return item?.Target ?? "";
        }

        return item.PathMode switch
        {
            PathMode.RelativeToApp => UnderRoot(AppRoot, item.Target),
            PathMode.InternalCopy => UnderRoot(InternalRoot, item.Target),
            _ => item.Target ?? ""
        };
    }

    /// <summary>把相对目标拼到基准目录下；已经是绝对路径就原样返回，不重复拼。</summary>
    private static string UnderRoot(string root, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "";
        }

        if (Path.IsPathRooted(target) || string.IsNullOrWhiteSpace(root))
        {
            return target;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(root, target));
        }
        catch (Exception)
        {
            return target;
        }
    }

    /// <summary>
    /// 把一个绝对路径表达成相对于 ClassIsland 数据根目录的形式。
    /// </summary>
    /// <returns>能相对化就返回相对路径，不在根目录下则返回 <c>null</c>。</returns>
    public static string? TryMakeAppRelative(string? absolutePath)
    {
        var root = AppRoot;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        try
        {
            var rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            {
                rootFull += Path.DirectorySeparatorChar;
            }

            var full = Path.GetFullPath(absolutePath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return full.StartsWith(rootFull, comparison) ? full[rootFull.Length..] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 把文件复制进插件内部目录。
    /// </summary>
    /// <returns>存放后的<b>文件名</b>（不是完整路径）——它就填进 <see cref="ShortcutItem.Target"/>。</returns>
    /// <remarks>
    /// 幂等：同名且内容一致的已经在里面了，就直接复用，不会越点越多。
    /// 同名但内容不同则会存成 <c>名字-2.ext</c>、<c>名字-3.ext</c>，
    /// 不覆盖用户之前存的副本。
    /// </remarks>
    public static string CopyIntoInternal(string sourcePath)
    {
        var root = InternalRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("插件数据目录还没准备好，稍后再试。");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到这个文件。", sourcePath);
        }

        if (!IsSupportedInternalFile(sourcePath))
        {
            throw new NotSupportedException("只支持视频 / 图片 / Office 单文件。");
        }

        Directory.CreateDirectory(root);

        var name = Path.GetFileName(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);

        for (var i = 1; i <= 999; i++)
        {
            var candidate = i == 1 ? name : $"{stem}-{i}{ext}";
            var target = Path.Combine(root, candidate);

            if (!File.Exists(target))
            {
                File.Copy(sourcePath, target, overwrite: false);
                return candidate;
            }

            if (IsSameFile(target, sourcePath))
            {
                return candidate;
            }
        }

        throw new IOException("同名文件太多了，先清理一下插件内部目录。");
    }

    /// <summary>列出内部目录里的全部文件（目录不存在就是空表）。</summary>
    public static List<string> ListInternalFiles()
    {
        var root = InternalRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 找出内部目录里已经没有任何快捷方式引用的副本。
    /// </summary>
    /// <remarks>
    /// 删掉一条快捷方式、或者把它从「内部副本」改回绝对路径之后，
    /// 那份拷贝就成了孤儿——视频动辄几百兆，得让用户能一键收回来。
    /// </remarks>
    public static List<string> FindUnusedInternalFiles(IEnumerable<ShortcutItem>? shortcuts)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in shortcuts ?? [])
        {
            if (item.Kind == ShortcutKind.Path && item.PathMode == PathMode.InternalCopy &&
                !string.IsNullOrWhiteSpace(item.Target))
            {
                used.Add(Path.GetFileName(item.Target));
            }
        }

        return [.. ListInternalFiles().Where(f => !used.Contains(Path.GetFileName(f)))];
    }

    /// <summary>两个文件是不是同一份内容。先比长度，再比 SHA-256。</summary>
    private static bool IsSameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length)
            {
                return false;
            }

            return HashOf(a) == HashOf(b);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
