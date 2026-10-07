using System.Diagnostics;

namespace WinIsland.Core.Diagnostics;

/// <summary>
/// Tiny logging facade. Writes to the debugger output only; a background utility
/// should not grow log files on disk unless asked to.
/// </summary>
public static class AppLog
{
    public static void Info(string source, string message) => Write("INF", source, message);

    public static void Warn(string source, string message, Exception? ex = null) =>
        Write("WRN", source, ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Error(string source, string message, Exception? ex = null) =>
        Write("ERR", source, ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string source, string message) =>
        Debug.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} [{source}] {message}");
}
