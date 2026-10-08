using System.Text.Json;

namespace WinIsland.Core.Claude;

/// <summary>
/// Where Claude Code (CLI, IDE extensions and the Claude desktop app) keeps its files. Claude Code
/// has used both <c>~/.claude</c> and <c>~/.config/claude</c>, and <c>CLAUDE_CONFIG_DIR</c> can move
/// it anywhere (several roots may be listed, comma-separated), so every candidate is considered.
/// </summary>
public static class ClaudePaths
{
    /// <summary>Config roots in priority order, without duplicates; existence is not checked.</summary>
    public static IReadOnlyList<string> ConfigRoots(string? configDirVariable, string home)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(configDirVariable))
        {
            foreach (string part in configDirVariable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                roots.Add(Environment.ExpandEnvironmentVariables(part));
            }
        }
        else
        {
            roots.Add(Path.Combine(home, ".claude"));
            roots.Add(Path.Combine(home, ".config", "claude"));
        }

        return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Directories that hold session transcripts (<c>*.jsonl</c>), for the roots that exist.</summary>
    public static IReadOnlyList<string> ProjectsDirectories(IEnumerable<string> roots, string? desktopSessionsDirectory) =>
        [.. roots.Select(r => Path.Combine(r, "projects"))
            .Concat(desktopSessionsDirectory is null ? [] : [desktopSessionsDirectory])
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The environment's roots: <c>CLAUDE_CONFIG_DIR</c> or the two default locations.</summary>
    public static IReadOnlyList<string> CurrentConfigRoots() =>
        ConfigRoots(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The Claude desktop app's local agent sessions (its Claude Code runs keep transcripts in there).</summary>
    public static string DesktopSessionsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "local-agent-mode-sessions");

    public static IReadOnlyList<string> CurrentProjectsDirectories() =>
        ProjectsDirectories(CurrentConfigRoots(), DesktopSessionsDirectory());

    /// <summary>Candidate credential files (<c>.credentials.json</c>) of the roots, existing ones only.</summary>
    public static IReadOnlyList<string> CredentialFiles(IEnumerable<string> roots) =>
        [.. roots.Select(r => Path.Combine(r, ".credentials.json")).Where(File.Exists)];
}

/// <summary>The Claude sign-in Claude Code stores locally (OAuth access token and when it lapses).</summary>
public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && at <= now;

    /// <summary>Reads <c>claudeAiOauth.accessToken</c> / <c>expiresAt</c> (epoch milliseconds); null if absent.</summary>
    public static ClaudeCredentials? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement oauth = root.TryGetProperty("claudeAiOauth", out JsonElement o) && o.ValueKind == JsonValueKind.Object ? o : root;
            if (!oauth.TryGetProperty("accessToken", out JsonElement token) || token.GetString() is not { Length: > 0 } accessToken)
            {
                return null;
            }

            DateTimeOffset? expires = oauth.TryGetProperty("expiresAt", out JsonElement e) && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64())
                : null;
            return new ClaudeCredentials(accessToken, expires);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>The answer of Claude's plan-usage endpoint (what <c>/usage</c> shows).</summary>
public static class ClaudePlanUsage
{
    public const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>Parses <c>five_hour</c> / <c>seven_day</c> windows ({utilization: 0–100, resets_at: ISO}); null if neither is present.</summary>
    public static ClaudeRateLimits? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            (double? Percent, DateTimeOffset? Resets) Window(string name)
            {
                if (!root.TryGetProperty(name, out JsonElement w) || w.ValueKind != JsonValueKind.Object)
                {
                    return (null, null);
                }

                double? percent = w.TryGetProperty("utilization", out JsonElement u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
                DateTimeOffset? resets = w.TryGetProperty("resets_at", out JsonElement r) && r.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
                    ? at
                    : null;
                return (percent, resets);
            }

            var five = Window("five_hour");
            var week = Window("seven_day");
            return five.Percent is null && week.Percent is null ? null : new ClaudeRateLimits(five.Percent, five.Resets, week.Percent, week.Resets);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
