using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MediaWidget;

internal static class Drawing
{
    private static readonly StringFormat GlyphFormat = CreateGlyphFormat();

    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Draws an icon-font glyph centred in <paramref name="bounds"/> with grayscale antialiasing.
    /// ClearType's subpixel rendering gives thin icon strokes red/blue fringes; Windows draws its icons in grayscale too.
    /// </summary>
    public static void Glyph(Graphics g, string glyph, Font font, Rectangle bounds, Color color)
    {
        var previous = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var brush = new SolidBrush(color))
            g.DrawString(glyph, font, brush, bounds, GlyphFormat);
        g.TextRenderingHint = previous;
    }

    private static StringFormat CreateGlyphFormat()
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        return format;
    }
}
