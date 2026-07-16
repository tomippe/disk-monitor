using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DiskMonitor.Helpers;

internal static class MenuIcons
{
    public static ImageSource? Refresh(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE72C", fg, size);
    public static ImageSource? Copy(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE8C8", fg, size);
    public static ImageSource? Info(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE946", fg, size);
    public static ImageSource? Feedback(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE90A", fg, size);
    public static ImageSource? Update(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE895", fg, size);
    public static ImageSource? Restart(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE777", fg, size);
    /// <summary>Mac TomippeRelaunch quit uses SF Symbol "power".</summary>
    public static ImageSource? Quit(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE7E8", fg, size);
    public static ImageSource? More(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE712", fg, size);
    public static ImageSource? Chevron(System.Windows.Media.Brush fg, double size = 12) => Glyph("\uE76C", fg, size);
    public static ImageSource? Recycle(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE74D", fg, size);
    public static ImageSource? Disk(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uEDA2", fg, size);
    public static ImageSource? Star(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE734", fg, size);
    public static ImageSource? Folder(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE8B7", fg, size);
    public static ImageSource? Eject(System.Windows.Media.Brush fg, double size = 16) => Glyph("\uE7EC", fg, size);

    private static ImageSource? Glyph(string glyph, System.Windows.Media.Brush foreground, double dipSize)
    {
        var typeface = ResolveIconTypeface();
        if (typeface is null) return null;

        var ft = new FormattedText(
            glyph,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            dipSize,
            foreground,
            1.0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawText(ft, new System.Windows.Point(
                Math.Max(0, (dipSize - ft.Width) / 2),
                Math.Max(0, (dipSize - ft.Height) / 2)));
        }

        var image = new DrawingImage(visual.Drawing);
        image.Freeze();
        return image;
    }

    private static Typeface? ResolveIconTypeface()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var ff = new System.Windows.Media.FontFamily(name);
            var tf = new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (tf.TryGetGlyphTypeface(out _))
                return tf;
        }
        return null;
    }
}
