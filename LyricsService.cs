using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaWidget;

internal sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Time-synced lyrics, sorted by time.</summary>
internal sealed class Lyrics(IReadOnlyList<LyricLine> lines)
{
    public IReadOnlyList<LyricLine> Lines { get; } = lines;

    /// <summary>Index of the line being sung at <paramref name="position"/>, or -1 before the first line.</summary>
    public int IndexAt(TimeSpan position)
    {
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }
}

/// <summary>
/// Looks up synced lyrics on LRCLIB (https://lrclib.net), a free, open lyrics database with no API key.
/// One request per new song; results (including "not found") are cached for the session.
/// </summary>
internal static partial class LyricsService
{
    private const int CacheSize = 100;
    // Versions whose length differs by more than this are probably a live cut, remix or radio edit.
    private static readonly TimeSpan MaxDurationDifference = TimeSpan.FromSeconds(8);

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://lrclib.net/"),
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "MediaWidget/1.0 (personal taskbar widget)" } },
    };

    private static readonly Dictionary<string, Lyrics?> Cache = [];
    private static readonly Queue<string> CacheOrder = new();

    /// <summary>Synced lyrics for the song, or null if LRCLIB has none. Throws on network errors.</summary>
    public static async Task<Lyrics?> FindAsync(string title, string artist, TimeSpan duration, CancellationToken cancellation)
    {
        string track = CleanTitle(title), mainArtist = MainArtist(artist);
        string key = $"{mainArtist}\n{track}".ToLowerInvariant();
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        string url = $"api/search?track_name={Uri.EscapeDataString(track)}&artist_name={Uri.EscapeDataString(mainArtist)}";
        using var response = await Http.GetAsync(url, cancellation);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);

        var best = PickBest(document.RootElement, duration);
        Remember(key, best);
        return best;
    }

    /// <summary>Chooses the synced lyrics from a search response whose length best matches the playing track.</summary>
    internal static Lyrics? PickBest(JsonElement results, TimeSpan duration)
    {
        Lyrics? best = null;
        var bestDifference = TimeSpan.MaxValue;
        if (results.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (item.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True)
                continue;
            if (!item.TryGetProperty("syncedLyrics", out var synced) || synced.ValueKind != JsonValueKind.String)
                continue;

            // Community-submitted entries can have "duration": null, so check the kind before reading it.
            // Entries with an unknown length are acceptable but rank below any verified length match.
            var difference = TimeSpan.Zero;
            if (duration > TimeSpan.Zero)
            {
                difference = item.TryGetProperty("duration", out var length)
                    && length.ValueKind == JsonValueKind.Number && length.TryGetDouble(out double seconds)
                    ? (TimeSpan.FromSeconds(seconds) - duration).Duration()
                    : MaxDurationDifference;
            }
            if (difference > MaxDurationDifference || difference >= bestDifference)
                continue;

            Lyrics parsed;
            try
            {
                parsed = Parse(synced.GetString()!);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                continue; // one malformed entry shouldn't hide the others
            }
            if (parsed.Lines.Count > 0)
            {
                best = parsed;
                bestDifference = difference;
            }
        }
        return best;
    }

    /// <summary>Appends a failed lookup to %LOCALAPPDATA%\MediaWidget\lyrics.log (kept small) for troubleshooting.</summary>
    public static void LogFailure(string title, string artist, Exception error)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWidget");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "lyrics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024)
                File.Delete(path);
            string detail = error.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : "";
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  \"{title}\" by {artist}: {error.GetType().Name}: {error.Message}{detail}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Parses LRC text: "[mm:ss.xx] line", possibly with several timestamps on one line.</summary>
    public static Lyrics Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (string raw in lrc.Split('\n'))
        {
            var stamps = Timestamp().Matches(raw);
            if (stamps.Count == 0)
                continue;
            var last = stamps[^1];
            string text = raw[(last.Index + last.Length)..].Trim();
            foreach (Match stamp in stamps)
            {
                // Skip a malformed timestamp rather than losing the whole song.
                if (!int.TryParse(stamp.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                    || !double.TryParse(stamp.Groups[2].Value.Replace(':', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double secs)
                    || minutes > 600)
                    continue;
                lines.Add(new LyricLine(TimeSpan.FromSeconds(minutes * 60 + secs), text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new Lyrics(lines);
    }

    // "Song (feat. X)" and "Song - Remastered 2011" are stored on LRCLIB as just "Song".
    private static string CleanTitle(string title)
    {
        string cleaned = FeaturingSuffix().Replace(title, "");
        cleaned = VersionSuffix().Replace(cleaned, "");
        return cleaned.Trim().Length > 0 ? cleaned.Trim() : title;
    }

    private static string MainArtist(string artist)
    {
        int cut = artist.IndexOfAny([',', '&']);
        return (cut > 0 ? artist[..cut] : artist).Trim();
    }

    private static void Remember(string key, Lyrics? lyrics)
    {
        if (Cache.TryAdd(key, lyrics))
            CacheOrder.Enqueue(key);
        while (CacheOrder.Count > CacheSize)
            Cache.Remove(CacheOrder.Dequeue());
    }

    [GeneratedRegex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingSuffix();

    [GeneratedRegex(@"\s+-\s+[^-]*\b(remaster(ed)?|live|version|edit|mix|mono|stereo)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();
}
