namespace WinIsland.Core.Helpers;

/// <summary>Text the user copied. Never written to disk.</summary>
public sealed record ClipboardEntry(string Text, DateTimeOffset CopiedAt);

/// <summary>The system clipboard (Win32 clipboard listener on Windows).</summary>
public interface IClipboardService
{
    /// <summary>Raised on the UI thread for each new text copy. Copies that password managers mark private are skipped.</summary>
    event EventHandler<ClipboardEntry>? Copied;

    /// <summary>Puts the text on the clipboard and pastes it into the window that has focus.</summary>
    Task PasteAsync(string text);

    Task CopyAsync(string text);
}

/// <summary>A browser download that is still being written.</summary>
public sealed record DownloadInfo(string PartialPath, string Name, long Bytes, DateTimeOffset UpdatedAt);

/// <summary>A finished download.</summary>
public sealed record CompletedDownload(string Path, string Name, long Bytes, DateTimeOffset CompletedAt);

/// <summary>Downloads folder watcher (Chrome/Edge .crdownload, Firefox .part…).</summary>
public interface IDownloadSource
{
    IReadOnlyList<DownloadInfo> Active { get; }

    /// <summary>Raised on any thread when <see cref="Active"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Raised on any thread when a download finishes.</summary>
    event EventHandler<CompletedDownload>? Completed;
}

/// <summary>Opens files and folders with the shell.</summary>
public interface IShellLauncher
{
    void Open(string path);

    /// <summary>Opens the containing folder with the item selected.</summary>
    void Reveal(string path);
}

public static class SizeText
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{bytes / 1024.0:0} KB"),
        < 1024L * 1024 * 1024 => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.0} MB"),
        _ => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.00} GB"),
    };

    public static string Ago(TimeSpan age) => age.TotalSeconds < 60 ? "now"
        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m"
        : age.TotalHours < 24 ? $"{(int)age.TotalHours}h"
        : $"{(int)age.TotalDays}d";

    /// <summary>Segoe Fluent Icons glyph for a file, by extension.</summary>
    public static string GlyphFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        "" when Directory.Exists(path) => "",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".heic" or ".svg" => "",
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" => "",
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" => "",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "",
        ".pdf" => "",
        ".exe" or ".msi" or ".msix" or ".appx" => "",
        ".doc" or ".docx" or ".txt" or ".md" or ".rtf" => "",
        ".xls" or ".xlsx" or ".csv" => "",
        ".cs" or ".js" or ".ts" or ".py" or ".json" or ".xml" or ".html" or ".css" => "",
        _ => "",
    };
}
