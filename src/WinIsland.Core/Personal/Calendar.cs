using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinIsland.Core.Personal;

/// <summary>One occurrence of a calendar event.</summary>
public sealed record CalendarEvent(string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string? Location, string? JoinUrl);

public interface ICalendarSource
{
    /// <summary>Occurrences from yesterday to two days ahead, sorted by start.</summary>
    IReadOnlyList<CalendarEvent> Events { get; }

    /// <summary>Raised on any thread.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Minimal iCalendar (RFC 5545) reader for subscription feeds (Google, Outlook, iCloud):
/// VEVENTs with time zones, all-day events, DAILY/WEEKLY/MONTHLY RRULEs with INTERVAL, COUNT,
/// UNTIL and BYDAY, EXDATE and cancelled events. Also finds the Teams / Zoom / Meet join link.
/// </summary>
public static partial class IcsParser
{
    private static readonly string[] JoinFields = ["X-MICROSOFT-SKYPETEAMSMEETINGURL", "X-GOOGLE-CONFERENCE", "URL", "LOCATION", "DESCRIPTION"];

    public static IReadOnlyList<CalendarEvent> Parse(string ics, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<CalendarEvent>();
        var overrides = new HashSet<(string Uid, DateTimeOffset Start)>();
        List<Dictionary<string, List<(string Params, string Value)>>> events = ReadEvents(Unfold(ics));

        // Edited single occurrences (RECURRENCE-ID) replace the generated one.
        foreach (var e in events)
        {
            if (Get(e, "RECURRENCE-ID") is { } rid && Get(e, "UID") is { } uid && ParseDate(rid.Params, rid.Value) is { } when)
            {
                overrides.Add((uid.Value, when.Start));
            }
        }

        foreach (var e in events)
        {
            if (string.Equals(Get(e, "STATUS")?.Value, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                Get(e, "DTSTART") is not { } dtStart || ParseDate(dtStart.Params, dtStart.Value) is not { } start)
            {
                continue;
            }

            TimeSpan length = Get(e, "DTEND") is { } dtEnd && ParseDate(dtEnd.Params, dtEnd.Value) is { } end
                ? end.Start - start.Start
                : Get(e, "DURATION") is { } duration ? ParseDuration(duration.Value) : start.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;

            string title = Unescape(Get(e, "SUMMARY")?.Value ?? "Busy");
            string? location = Get(e, "LOCATION")?.Value is { Length: > 0 } l ? Unescape(l) : null;
            string? join = FindJoinUrl(e);
            string? uidValue = Get(e, "UID")?.Value;
            bool isOverride = Get(e, "RECURRENCE-ID") is not null;
            var excluded = new HashSet<DateTimeOffset>(
                All(e, "EXDATE").SelectMany(x => x.Value.Split(',').Select(v => ParseDate(x.Params, v)?.Start)).OfType<DateTimeOffset>());

            IEnumerable<DateTimeOffset> starts = Get(e, "RRULE") is { } rule && !isOverride
                ? Expand(start.Start, rule.Value, from - length, to)
                : [start.Start];

            foreach (DateTimeOffset occurrence in starts)
            {
                if (excluded.Contains(occurrence) || (!isOverride && uidValue is not null && Get(e, "RRULE") is not null && overrides.Contains((uidValue, occurrence))))
                {
                    continue;
                }

                DateTimeOffset occurrenceEnd = occurrence + length;
                if (occurrenceEnd > from && occurrence < to)
                {
                    result.Add(new CalendarEvent(title, occurrence, occurrenceEnd, start.AllDay, location, join));
                }
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    internal static string? FindJoinUrl(Dictionary<string, List<(string Params, string Value)>> e)
    {
        string text = string.Join("\n", JoinFields.SelectMany(k => All(e, k).Select(v => Unescape(v.Value))));
        return JoinLink().Match(text) is { Success: true } m ? m.Value.TrimEnd('>', ')', '.', ',') : null;
    }

    private static List<string> Unfold(string ics)
    {
        var lines = new List<string>();
        foreach (string raw in ics.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && lines.Count > 0)
            {
                lines[^1] += raw[1..];
            }
            else
            {
                lines.Add(raw);
            }
        }

        return lines;
    }

    private static List<Dictionary<string, List<(string, string)>>> ReadEvents(List<string> lines)
    {
        var events = new List<Dictionary<string, List<(string, string)>>>();
        Dictionary<string, List<(string, string)>>? current = null;
        int depth = 0;
        foreach (string line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new(StringComparer.OrdinalIgnoreCase);
                depth = 0;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            // Skip nested components (VALARM).
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
                continue;
            }

            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                events.Add(current);
                current = null;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            int colon = FindValueColon(line);
            if (colon <= 0)
            {
                continue;
            }

            string head = line[..colon];
            int semi = head.IndexOf(';', StringComparison.Ordinal);
            string name = semi >= 0 ? head[..semi] : head;
            string parameters = semi >= 0 ? head[(semi + 1)..] : string.Empty;
            if (!current.TryGetValue(name, out var values))
            {
                current[name] = values = [];
            }

            values.Add((parameters, line[(colon + 1)..]));
        }

        return events;
    }

    /// <summary>The first colon outside quoted parameter values.</summary>
    private static int FindValueColon(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private static (string Params, string Value)? Get(Dictionary<string, List<(string Params, string Value)>> e, string name) =>
        e.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    private static List<(string Params, string Value)> All(Dictionary<string, List<(string Params, string Value)>> e, string name) =>
        e.TryGetValue(name, out var values) ? values : [];

    internal static (DateTimeOffset Start, bool AllDay)? ParseDate(string parameters, string value)
    {
        value = value.Trim();
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
        {
            // All-day: midnight local time.
            return (new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day)), true);
        }

        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
        {
            return null;
        }

        if (value.EndsWith('Z'))
        {
            return (new DateTimeOffset(time, TimeSpan.Zero), false);
        }

        TimeZoneInfo zone = TimeZoneInfo.Local;
        if (TzidOf(parameters) is { } tzid)
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(tzid);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                zone = TimeZoneInfo.Local;
            }
        }

        return (new DateTimeOffset(time, zone.GetUtcOffset(time)), false);
    }

    private static string? TzidOf(string parameters)
    {
        foreach (string p in parameters.Split(';'))
        {
            if (p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
            {
                return p[5..].Trim('"');
            }
        }

        return null;
    }

    internal static TimeSpan ParseDuration(string value)
    {
        Match m = Duration().Match(value);
        if (!m.Success)
        {
            return TimeSpan.Zero;
        }

        int Part(int group) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        var span = new TimeSpan((Part(1) * 7) + Part(2), Part(3), Part(4), Part(5));
        return value.StartsWith('-') ? -span : span;
    }

    /// <summary>Occurrences of an RRULE that start before <paramref name="to"/> and could overlap from <paramref name="from"/>.</summary>
    internal static IEnumerable<DateTimeOffset> Expand(DateTimeOffset start, string rule, DateTimeOffset from, DateTimeOffset to)
    {
        Dictionary<string, string> parts = rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        string freq = parts.GetValueOrDefault("FREQ", string.Empty).ToUpperInvariant();
        int interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out int i) && i > 0 ? i : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), out int c) ? c : null;
        DateTimeOffset? until = parts.TryGetValue("UNTIL", out string? u) && ParseDate(string.Empty, u) is { } ud ? ud.Start : null;
        DayOfWeek[] byDay = parts.TryGetValue("BYDAY", out string? bd)
            ? [.. bd.Split(',').Select(d => d.Trim()[^2..].ToUpperInvariant() switch
            {
                "MO" => DayOfWeek.Monday,
                "TU" => DayOfWeek.Tuesday,
                "WE" => DayOfWeek.Wednesday,
                "TH" => DayOfWeek.Thursday,
                "FR" => DayOfWeek.Friday,
                "SA" => DayOfWeek.Saturday,
                _ => DayOfWeek.Sunday,
            })]
            : [];

        // Walk in local wall-clock time so a 9:00 meeting stays at 9:00 across DST changes.
        TimeSpan offset = start.Offset;
        DateTime local = start.DateTime;
        int produced = 0;
        for (int step = 0; step < 5000; step++)
        {
            IEnumerable<DateTime> candidates = freq switch
            {
                "DAILY" => [local.AddDays(step * interval)],
                "WEEKLY" when byDay.Length > 0 => WeekDays(local, step * interval, byDay),
                "WEEKLY" => [local.AddDays(step * 7 * interval)],
                "MONTHLY" => [local.AddMonths(step * interval)],
                "YEARLY" => [local.AddYears(step * interval)],
                _ => step == 0 ? [local] : [],
            };

            bool any = false;
            foreach (DateTime candidate in candidates)
            {
                any = true;
                if (candidate < local)
                {
                    continue;
                }

                var occurrence = new DateTimeOffset(candidate, offset);
                if ((until is { } end && occurrence > end) || (count is { } max && produced >= max) || occurrence >= to)
                {
                    yield break;
                }

                produced++;
                if (occurrence >= from)
                {
                    yield return occurrence;
                }
            }

            if (!any)
            {
                yield break;
            }
        }
    }

    private static IEnumerable<DateTime> WeekDays(DateTime start, int weekOffset, DayOfWeek[] days)
    {
        DateTime weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7)).AddDays(weekOffset * 7);
        return days
            .Select(d => weekStart.AddDays(((int)d + 6) % 7) + start.TimeOfDay)
            .OrderBy(d => d);
    }

    private static string Unescape(string value) => new StringBuilder(value)
        .Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\")
        .ToString();

    [GeneratedRegex(@"https://(?:teams\.microsoft\.com/l/meetup-join/|teams\.live\.com/meet/|[\w.-]*zoom\.us/(?:j|my|w)/|meet\.google\.com/[a-z]{3}-|[\w.-]*webex\.com/(?:meet|join|[\w.-]+/j\.php))[^\s""<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex JoinLink();

    [GeneratedRegex(@"^-?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$")]
    private static partial Regex Duration();
}
