namespace WinIsland.App.Services;

/// <summary>
/// Diagnostic switches read from the WINISLAND_EXPERIMENT environment variable (comma-separated),
/// used by CI to compare window configurations side by side. Unset in normal use.
/// </summary>
internal static class Experiments
{
    private static readonly HashSet<string> s_flags = new(
        (Environment.GetEnvironmentVariable("WINISLAND_EXPERIMENT") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    public static bool Has(string flag) => s_flags.Contains(flag);

    public static string Describe() => s_flags.Count == 0 ? "none" : string.Join(",", s_flags);
}
