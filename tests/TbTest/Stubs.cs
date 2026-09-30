// 只为了让「设置页」能在测试工程里编译：替掉两个测试环境给不出来的依赖
// （拍照要 WinRT，宿主服务要 ClassIsland 的整套 DI）。接口按设置页实际用到的成员来。
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ClassIsland.Toolbox.Models;

namespace ClassIsland.Toolbox.Services;

public sealed record CameraDevice(string Id, string Name);

public sealed class ShotResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Diagnostics { get; init; } = "";
    public Bitmap? Annotated { get; init; }
}

public static class CameraPicker
{
    public static bool IsWarm => false;
    public static Task<List<CameraDevice>> ListCamerasAsync() => Task.FromResult(new List<CameraDevice>());
    public static Task ShutdownCameraAsync() => Task.CompletedTask;
    public static void ForgetRecent() { }
    public static Task<ShotResult> CaptureAndPickAsync(PickSettings s, string dir, string cfg, bool annotate = false) =>
        Task.FromResult(new ShotResult { Success = false, Message = "测试环境无摄像头" });
}

public class ToolboxHostService
{
    public static string PluginDirectory => "/tmp";
    public ToolboxSettings Settings { get; } = new();
    public PickSettings Pick => Settings.Pick;
    public PickStats Stats { get; } = new();
    public RosterService? Roster => null;
    public string RosterPath => "/tmp/名单.txt";
    public string StatsPath => "/tmp/幸运抽签统计.json";
    public string ConfigFolder => "/tmp";
    public void SaveSettings() { }
    public void SaveStats() { }
    public void SaveAll() { }
    public void ShowStatsWindow() { }
    public void ApplySizeToWindow() { }
    public void ApplyOpacityToWindow() { }
    public void NotifyShortcutsChanged() { }
    public void ApplyLuckyDrawEnabled() { }
}
