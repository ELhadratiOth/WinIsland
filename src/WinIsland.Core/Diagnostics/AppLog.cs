using System.Diagnostics;

namespace WinIsland.Core.Diagnostics;

/// <summary>
/// Tiny logging facade. Always writes to the debugger; once <see cref="EnableFile"/> is
/// called it also appends to a small, size-capped log file so problems on users' machines
/// can be diagnosed.
/// </summary>
public static class AppLog
{
    private const long MaxFileBytes = 512 * 1024;
    private static readonly Lock s_gate = new();
    private static string? s_path;

    public static string? FilePath => s_path;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland", "winisland.log");

    /// <summary>Starts appending to <paramref name="path"/>; a previous oversized log is rotated to <c>.old</c>.</summary>
    public static void EnableFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                File.Move(path, path + ".old", overwrite: true);
            }

            s_path = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Log file unavailable: {ex.Message}");
        }
    }

    public static void Info(string source, string message) => Write("INF", source, message);

    public static void Warn(string source, string message, Exception? ex = null) =>
        Write("WRN", source, ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Error(string source, string message, Exception? ex = null) =>
        Write("ERR", source, ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string source, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{source}] {message}";
        Debug.WriteLine(line);

        string? path = s_path;
        if (path is null)
        {
            return;
        }

        lock (s_gate)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
