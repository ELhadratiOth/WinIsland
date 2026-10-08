using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinIsland.Core.Media;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Lyrics for one track. <see cref="IsSynced"/> lines carry real timestamps.</summary>
public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, bool IsSynced)
{
    /// <summary>Index of the line being sung at <paramref name="position"/> (-1 before the first).</summary>
    public int IndexAt(TimeSpan position)
    {
        if (!IsSynced)
        {
            return -1;
        }

        int index = -1;
        for (int i = 0; i < Lines.Count && Lines[i].Time <= position; i++)
        {
            index = i;
        }

        return index;
    }
}

public interface ILyricsProvider
{
    /// <summary>Null when none are found (or the lookup is switched off).</summary>
    Task<Lyrics?> GetAsync(string artist, string title, string? album, TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>Parses the LRC format: "[mm:ss.xx] line", possibly several stamps per line.</summary>
public static partial class LrcParser
{
    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return lines;
        }

        foreach (string raw in lrc.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            MatchCollection stamps = Stamp().Matches(line);
            if (stamps.Count == 0)
            {
                continue;
            }

            string text = line[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match stamp in stamps)
            {
                int minutes = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                double seconds = double.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds), text));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]")]
    private static partial Regex Stamp();
}

/// <summary>
/// LRCLIB (lrclib.net), a free community lyrics database with time-synced lyrics. Only the
/// artist, title, album and duration are sent.
/// </summary>
public static class LrcLib
{
    public static Uri BuildGetUri(string artist, string title, string? album, TimeSpan duration)
    {
        string query = $"artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrWhiteSpace(album))
        {
            query += $"&album_name={Uri.EscapeDataString(album)}";
        }

        if (duration > TimeSpan.Zero)
        {
            query += string.Create(CultureInfo.InvariantCulture, $"&duration={(int)Math.Round(duration.TotalSeconds)}");
        }

        return new Uri($"https://lrclib.net/api/get?{query}");
    }

    public static Uri BuildSearchUri(string artist, string title) =>
        new($"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}");

    /// <summary>Parses a single /api/get record.</summary>
    public static Lyrics? ParseRecord(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return FromRecord(document.RootElement);
    }

    /// <summary>Picks the best record of an /api/search result: synced first.</summary>
    public static Lyrics? ParseSearch(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Lyrics? plain = null;
        foreach (JsonElement record in document.RootElement.EnumerateArray())
        {
            Lyrics? lyrics = FromRecord(record);
            if (lyrics is { IsSynced: true })
            {
                return lyrics;
            }

            plain ??= lyrics;
        }

        return plain;
    }

    private static Lyrics? FromRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (record.TryGetProperty("syncedLyrics", out JsonElement synced) && synced.ValueKind == JsonValueKind.String)
        {
            IReadOnlyList<LyricLine> lines = LrcParser.Parse(synced.GetString());
            if (lines.Count > 0)
            {
                return new Lyrics(lines, IsSynced: true);
            }
        }

        if (record.TryGetProperty("plainLyrics", out JsonElement plain) && plain.ValueKind == JsonValueKind.String &&
            plain.GetString() is { Length: > 0 } text)
        {
            return new Lyrics(
                [.. text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Select(l => new LyricLine(TimeSpan.Zero, l))],
                IsSynced: false);
        }

        return null;
    }
}
