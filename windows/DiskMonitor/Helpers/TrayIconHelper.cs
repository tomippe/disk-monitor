using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace DiskMonitor.Helpers;

public static class TrayIconHelper
{
    private const string EmbeddedIconName = "DiskMonitor.Assets.app-icon.png";
    private static readonly int[] TraySizes = [16, 20, 24, 32, 40, 48];

    public static Icon CreateTrayIcon()
    {
        using var source = LoadSourceBitmap();
        using var stream = new MemoryStream();
        WriteIco(stream, source);
        stream.Position = 0;
        using var loaded = new Icon(stream);
        return (Icon)loaded.Clone();
    }

    public static void WriteAppIco(string path)
    {
        using var source = LoadSourceBitmap();
        using var fs = File.Create(path);
        WriteIco(fs, source);
    }

    private static Bitmap LoadSourceBitmap()
    {
        using var stream = OpenEmbeddedIconStream();
        return new Bitmap(stream);
    }

    private static Stream OpenEmbeddedIconStream()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceStream(EmbeddedIconName)
            ?? throw new InvalidOperationException($"Embedded icon not found: {EmbeddedIconName}");
    }

    private static void WriteIco(Stream stream, Bitmap source)
    {
        var pngs = TraySizes.Select(size => RenderPng(source, size)).ToList();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)pngs.Count);

        var offset = 6 + 16 * pngs.Count;
        for (var i = 0; i < TraySizes.Length; i++)
        {
            var size = TraySizes[i];
            var png = pngs[i];
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }

        foreach (var png in pngs)
            writer.Write(png);
    }

    private static byte[] RenderPng(Bitmap source, int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(source, 0, 0, size, size);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
