// 教学助手 v1.0.0.0 —— ClassIsland 置顶工具条插件：幸运抽签、屏幕批注、自定义快捷方式
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassIsland.Toolbox.Models;

/// <summary>
/// 插件各类数据文件的公共读写方式。
/// </summary>
/// <remarks>
/// 抽出来是因为原来三个模型类各自抄了一份 <see cref="JsonSerializerOptions"/>，
/// 而且写盘方式还不一样：<c>ToolboxSettings</c> 是原子的，<c>PickSettings</c> 直接覆写。
/// 同一件事有两种写法，早晚会有人改漏一处。
/// <para/>
/// 这里定死两件事：<b>序列化选项只有一份</b>，<b>写盘一律原子</b>。
/// </remarks>
internal static class JsonStorage
{
    /// <summary>
    /// 全插件统一的序列化选项。
    /// </summary>
    /// <remarks>
    /// <c>UnsafeRelaxedJsonEscaping</c> 是必须的：默认的转义器会把中文写成 <c>\uXXXX</c>，
    /// 配置文件打开一看全是乱码，老师想手工改个名单都没法改。
    /// 枚举写成名字而不是数字，是为了让配置文件自己说明白。
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 原子写：先写同目录下的 <c>.tmp</c>，再整体替换过去。
    /// </summary>
    /// <remarks>
    /// 这样任何时刻磁盘上的正式文件要么是旧的完整内容、要么是新的完整内容，
    /// 不会出现"写到一半被杀进程"留下的半截 JSON —— 那种文件下次启动是读不出来的。
    /// <para/>
    /// <b>写失败一律吞掉。</b>调用方都是"顺手存一下"，存不上顶多丢一次改动，
    /// 绝不能因为磁盘满或者没权限就把抽签、批注这些主流程打断。
    /// </remarks>
    public static void WriteAtomic<T>(string path, T value)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            // 存不上就下次再存。
        }
    }

    /// <summary>
    /// 读一个文本文件。文件不存在或读不出来都返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// "读不出来"（被占用、没权限）和"内容坏了"是两回事，调用方要分开处理：
    /// 前者不该留任何垃圾文件，后者才值得留一份备份给用户翻。
    /// </remarks>
    public static string? TryReadAll(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 把读不懂的文件留一份 <c>.bad</c> 备份。
    /// </summary>
    /// <remarks>
    /// 直接删掉重建的话，用户连"数据曾经存在过"都不知道，
    /// 只会觉得插件把一学期的记录吃了。
    /// </remarks>
    public static void KeepBrokenCopy(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bad", overwrite: true);
            }
        }
        catch (Exception)
        {
            // 备份失败也不能拦住插件启动。
        }
    }
}
