using Microsoft.Win32;

namespace MediaWidget;

/// <summary>
/// Colours that follow the taskbar's light/dark mode and the system accent colour, optionally tinted
/// with the current album's colour.
/// </summary>
internal sealed class Theme
{
    public bool IsLight { get; private init; }
    /// <summary>Background gradient start (left, or top on a vertical taskbar).</summary>
    public Color Background { get; private init; }
    /// <summary>Background gradient end; equals <see cref="Background"/> for a flat fill.</summary>
    public Color BackgroundEnd { get; private init; }
    public Color Foreground { get; private init; }
    public Color ForegroundDim { get; private init; }
    public Color ForegroundDisabled { get; private init; }
    public Color HoverBackground { get; private init; }
    public Color PlayHover { get; private init; }
    public Color Track { get; private init; }
    public Color ProgressFill { get; private init; }
    public Color Accent { get; private init; }

    public static Theme Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool light = key?.GetValue("SystemUsesLightTheme") is int value && value == 1;

        try
        {
            Native.SetPreferredAppMode(light ? 3 /* ForceLight */ : 2 /* ForceDark */);
            Native.FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
        }

        var background = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
        var foreground = light ? Color.FromArgb(26, 26, 26) : Color.White;
        return new Theme
        {
            IsLight = light,
            Background = background,
            BackgroundEnd = background,
            Foreground = foreground,
            ForegroundDim = light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(170, 170, 170),
            ForegroundDisabled = light ? Color.FromArgb(180, 180, 180) : Color.FromArgb(90, 90, 90),
            HoverBackground = light ? Color.FromArgb(225, 225, 225) : Color.FromArgb(55, 55, 55),
            PlayHover = light ? Color.FromArgb(70, 70, 70) : Color.FromArgb(215, 215, 215),
            Track = light ? Color.FromArgb(200, 200, 200) : Color.FromArgb(80, 80, 80),
            ProgressFill = foreground,
            // The system accent can be anything (e.g. dark grey), so make sure it's readable as text.
            Accent = ColorMath.EnsureContrast(
                ReadAccent(light) ?? (light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255)),
                background, MinTextContrast, lighten: !light),
        };
    }

    /// <summary>
    /// Above the 4.5:1 WCAG minimum because the lyric line is small (12px) text read at a glance on the taskbar.
    /// </summary>
    private const double MinTextContrast = 6.0;

    /// <summary>
    /// This theme re-coloured around an album colour. Lightness is fixed per role so text stays readable
    /// whatever the cover looks like; only hue and (capped) saturation come from the album.
    /// </summary>
    public Theme Tinted(Color album)
    {
        float h = album.GetHue(), s = album.GetSaturation();
        Color Tone(float maxSaturation, float lightness) => ColorMath.FromHsl(h, Math.Min(s, maxSaturation), lightness);
        // Faintly coloured covers get a soft accent; vivid ones a strong one. Hues like blue and purple look much
        // darker than their lightness suggests, so check real contrast against the lighter end of the background.
        var background = IsLight ? Tone(0.55f, 0.88f) : Tone(0.55f, 0.21f);
        var accent = ColorMath.EnsureContrast(
            ColorMath.FromHsl(h, Math.Clamp(s * 1.6f, 0.30f, 0.85f), IsLight ? 0.38f : 0.68f),
            background, MinTextContrast, lighten: !IsLight);

        return new Theme
        {
            IsLight = IsLight,
            Background = background,
            BackgroundEnd = IsLight ? Tone(0.35f, 0.95f) : Tone(0.40f, 0.12f),
            Foreground = Foreground,
            ForegroundDim = IsLight ? Tone(0.20f, 0.32f) : Tone(0.20f, 0.74f),
            ForegroundDisabled = IsLight ? Tone(0.15f, 0.70f) : Tone(0.15f, 0.40f),
            HoverBackground = IsLight ? Tone(0.45f, 0.82f) : Tone(0.45f, 0.29f),
            PlayHover = PlayHover,
            Track = IsLight ? Tone(0.35f, 0.76f) : Tone(0.35f, 0.33f),
            ProgressFill = accent,
            Accent = accent,
        };
    }

    public static Theme Lerp(Theme a, Theme b, float t)
    {
        Color L(Color x, Color y) => ColorMath.Lerp(x, y, t);
        return new Theme
        {
            IsLight = b.IsLight,
            Background = L(a.Background, b.Background),
            BackgroundEnd = L(a.BackgroundEnd, b.BackgroundEnd),
            Foreground = L(a.Foreground, b.Foreground),
            ForegroundDim = L(a.ForegroundDim, b.ForegroundDim),
            ForegroundDisabled = L(a.ForegroundDisabled, b.ForegroundDisabled),
            HoverBackground = L(a.HoverBackground, b.HoverBackground),
            PlayHover = L(a.PlayHover, b.PlayHover),
            Track = L(a.Track, b.Track),
            ProgressFill = L(a.ProgressFill, b.ProgressFill),
            Accent = L(a.Accent, b.Accent),
        };
    }

    public static void AllowDarkMenus(IntPtr hwnd)
    {
        try { Native.AllowDarkModeForWindow(hwnd, true); }
        catch (EntryPointNotFoundException) { }
    }

    private static Color? ReadAccent(bool light)
    {
        // AccentPalette holds 8 RGBA shades from lightest to darkest; Windows uses a light shade on dark surfaces.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
        if (key?.GetValue("AccentPalette") is not byte[] palette || palette.Length < 32)
            return null;
        int i = (light ? 4 : 1) * 4;
        return Color.FromArgb(palette[i], palette[i + 1], palette[i + 2]);
    }
}

internal static class ColorMath
{
    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    public static double Luminance(Color c)
    {
        static double Channel(int value)
        {
            double v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio, from 1:1 (identical) to 21:1 (black on white).</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Lightens (or darkens) <paramref name="foreground"/>, keeping its hue and saturation, until it reaches
    /// <paramref name="minRatio"/> contrast against <paramref name="background"/>.
    /// </summary>
    public static Color EnsureContrast(Color foreground, Color background, double minRatio, bool lighten)
    {
        float h = foreground.GetHue(), s = foreground.GetSaturation(), l = foreground.GetBrightness();
        var color = foreground;
        while (Contrast(color, background) < minRatio && l is > 0f and < 1f)
        {
            l = Math.Clamp(l + (lighten ? 0.02f : -0.02f), 0f, 1f);
            color = FromHsl(h, s, l);
        }
        return color;
    }

    public static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (int)MathF.Round(a.R + (b.R - a.R) * t),
        (int)MathF.Round(a.G + (b.G - a.G) * t),
        (int)MathF.Round(a.B + (b.B - a.B) * t));

    /// <param name="h">Hue in degrees (0-360).</param>
    /// <param name="s">Saturation (0-1).</param>
    /// <param name="l">Lightness (0-1).</param>
    public static Color FromHsl(float h, float s, float l)
    {
        float c = (1 - Math.Abs(2 * l - 1)) * s;
        float x = c * (1 - Math.Abs(h / 60f % 2 - 1));
        float m = l - c / 2;
        (float r, float g, float b) = (h % 360) switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        static int Byte(float v) => Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return Color.FromArgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    /// <summary>
    /// The cover's most prominent colour, or null for black-and-white / greyscale covers.
    /// Pixels are weighted by chroma (how far they are from grey), so bright green lettering on a black
    /// cover counts strongly while black, white and grey count for nothing. Pixels are grouped by hue and
    /// the heaviest hue wins. Samples at most ~128x128 points of <paramref name="area"/>, so it's cheap on
    /// full-size covers, and full size keeps thin text and small logos from blurring away.
    /// </summary>
    public static Color? Dominant(Bitmap image, Rectangle area)
    {
        // minChroma sits just above JPEG colour noise, so washed-out covers still get a (subtle) tint.
        const int bins = 24, maxSamplesPerAxis = 128, minChroma = 14;
        var weight = new double[bins];
        var sumR = new double[bins];
        var sumG = new double[bins];
        var sumB = new double[bins];
        double total = 0;
        int samples = 0;

        int step = Math.Max(1, Math.Max(area.Width, area.Height) / maxSamplesPerAxis);
        var data = image.LockBits(area, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[area.Width];
            for (int y = 0; y < area.Height; y += step)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < row.Length; x += step)
                {
                    samples++;
                    var c = Color.FromArgb(row[x]);
                    if (c.A < 200)
                        continue;
                    int chroma = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
                    if (chroma < minChroma)
                        continue; // grey, black, white, or JPEG noise
                    double w = chroma / 255.0 * (chroma / 255.0);
                    int bin = (int)(c.GetHue() / 360f * bins) % bins;
                    weight[bin] += w;
                    sumR[bin] += c.R * w;
                    sumG[bin] += c.G * w;
                    sumB[bin] += c.B * w;
                    total += w;
                }
            }
        }
        finally
        {
            image.UnlockBits(data);
        }

        // A small logo or line of text is enough, but a few stray pixels on a greyscale cover aren't.
        if (total < samples * 0.004)
            return null;

        int best = 0;
        double bestScore = 0;
        for (int i = 0; i < bins; i++)
        {
            if (weight[i] <= 0)
                continue;
            double score = weight[i] + 0.5 * (weight[(i + 1) % bins] + weight[(i + bins - 1) % bins]);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        double wb = weight[best];
        return wb <= 0 ? null : Color.FromArgb((int)(sumR[best] / wb), (int)(sumG[best] / wb), (int)(sumB[best] / wb));
    }
}
