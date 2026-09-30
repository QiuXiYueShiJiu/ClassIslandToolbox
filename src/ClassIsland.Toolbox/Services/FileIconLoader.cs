using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClassIsland.Toolbox.Services;

/// <summary>
/// 取出 Windows 资源管理器里显示的那个文件图标，交给工具条按钮用。
/// </summary>
/// <remarks>
/// <b>为什么自己写 P/Invoke 而不是引 System.Drawing：</b>
/// <c>System.Drawing.Common</c> 从 .NET 7 起在非 Windows 上直接抛异常，
/// 为了一个图标把整个包拖上一个几十兆的原生依赖不划算。这里的调用面很小，
/// 而且外面包了完整的降级——取不到就退回 emoji，绝不会因为图标把插件带崩。
/// <para/>
/// <b>只在 Windows 上真正干活。</b>其它平台直接返回 <c>null</c>，
/// 由调用方退回到「用户填的图标 / 类型默认图标」。
/// <para/>
/// 结果按路径缓存：工具条每次重建按钮都要取一遍图标，
/// 不缓存的话改一次设置就要打十几次 shell 调用。
/// </remarks>
internal static class FileIconLoader
{
    private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// 取指定路径的文件图标。取不到返回 <c>null</c>（调用方负责降级）。
    /// </summary>
    public static Avalonia.Media.Imaging.Bitmap? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        Avalonia.Media.Imaging.Bitmap? icon = null;
        try
        {
            icon = OperatingSystem.IsWindows() ? LoadWindows(path) : null;
        }
        catch (Exception)
        {
            // 图标是锦上添花，任何一步出问题都退回到 emoji。
            icon = null;
        }

        lock (Gate)
        {
            Cache[path] = icon;
        }

        return icon;
    }

    #region Windows 实现

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint DiNormal = 0x0003;
    private const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Bitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, ref Win32Bitmap b);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    private static Avalonia.Media.Imaging.Bitmap? LoadWindows(string path)
    {
        var info = new ShFileInfo();
        var size = (uint)Marshal.SizeOf<ShFileInfo>();

        // 路径不存在（用户手打错了）时 SHGetFileInfo 会失败，那就没什么可取的。
        if (SHGetFileInfo(path, 0, ref info, size, ShgfiIcon | ShgfiLargeIcon) == IntPtr.Zero ||
            info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        var hIcon = info.hIcon;
        var hbmColor = IntPtr.Zero;
        var hbmMask = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var dc = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            // 图标的原生尺寸要从它的颜色位图问出来，不能假定是 32×32。
            var width = 32;
            var height = 32;
            if (GetIconInfo(hIcon, out var iconInfo))
            {
                hbmColor = iconInfo.hbmColor;
                hbmMask = iconInfo.hbmMask;

                if (hbmColor != IntPtr.Zero)
                {
                    var bmp = new Win32Bitmap();
                    if (GetObject(hbmColor, Marshal.SizeOf<Win32Bitmap>(), ref bmp) != 0 &&
                        bmp.bmWidth > 0 && bmp.bmHeight > 0)
                    {
                        width = bmp.bmWidth;
                        height = bmp.bmHeight;
                    }
                }
            }

            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                return null;
            }

            // biHeight 取负 = 自上而下的 DIB，省得后面再翻转行序。
            var bmi = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = DibRgbColors
                }
            };

            dib = CreateDIBSection(dc, ref bmi, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = SelectObject(dc, dib);

            // DrawIconEx 画进 32bpp DIB 的结果是**预乘 alpha** 的 BGRA，
            // 所以下面建 WriteableBitmap 时也要声明成 Premul，否则半透明边缘会发黑。
            if (!DrawIconEx(dc, 0, 0, hIcon, width, height, 0, IntPtr.Zero, DiNormal))
            {
                return null;
            }

            var stride = width * 4;
            var buffer = new byte[stride * height];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            return ToBitmap(buffer, width, height, stride);
        }
        finally
        {
            if (dc != IntPtr.Zero && previous != IntPtr.Zero)
            {
                SelectObject(dc, previous);
            }

            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
            }

            if (dc != IntPtr.Zero)
            {
                DeleteDC(dc);
            }

            // GetIconInfo 复制出来的位图要自己删；hIcon 是 SHGetFileInfo 给的，也要还回去。
            if (hbmColor != IntPtr.Zero)
            {
                DeleteObject(hbmColor);
            }

            if (hbmMask != IntPtr.Zero)
            {
                DeleteObject(hbmMask);
            }

            DestroyIcon(hIcon);
        }
    }

    private static Avalonia.Media.Imaging.Bitmap ToBitmap(byte[] bgra, int width, int height, int stride)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var locked = bitmap.Lock())
        {
            if (locked.RowBytes == stride)
            {
                Marshal.Copy(bgra, 0, locked.Address, bgra.Length);
            }
            else
            {
                // 显存那边的行距不一定等于 width*4，得逐行搬。
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(bgra, y * stride, locked.Address + y * locked.RowBytes, stride);
                }
            }
        }

        return bitmap;
    }

    #endregion
}
