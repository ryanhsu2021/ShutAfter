using System;
using System.IO;
using System.Reflection;

namespace ShutAfter.Core;

/// <summary>
/// 统一图标来源：exe 内嵌的 Assets\ShutAfter.ico（下载箭头 + 时钟）。
/// 运行时按目标尺寸取对应帧，避免 GDI 绘制与缩放模糊。
/// </summary>
public static class IconFactory
{
    private static byte[]? _bytes;

    private static byte[] Bytes
    {
        get
        {
            if (_bytes != null) return _bytes;

            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("ShutAfter.ico");
            if (stream == null) return Array.Empty<byte>();

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _bytes = ms.ToArray();
            return _bytes;
        }
    }

    /// <summary>按指定边长取图标帧；找不到资源时返回 null，由调用方降级。</summary>
    public static System.Drawing.Icon? Create(int size = 16)
    {
        var data = Bytes;
        if (data.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(data);
            return new System.Drawing.Icon(ms, new System.Drawing.Size(size, size));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>托盘图标：跟随系统小图标尺寸（高 DPI 下会是 20/24 像素）。</summary>
    public static System.Drawing.Icon? CreateTray()
    {
        int size;
        try
        {
            size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        }
        catch
        {
            size = 16;
        }

        return Create(size) ?? Create(16);
    }

    /// <summary>WPF 窗口 / 任务栏图标，取 32 像素帧。</summary>
    public static System.Windows.Media.ImageSource? CreateWindowIcon(int size = 32)
    {
        using var ico = Create(size);
        if (ico == null) return null;

        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                ico.Handle, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
