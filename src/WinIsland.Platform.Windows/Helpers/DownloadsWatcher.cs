using System.Runtime.InteropServices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Helpers;
using WinIsland.Core.Integrations;

namespace WinIsland.Platform.Windows.Helpers;

/// <summary>
/// Browser downloads in the Downloads folder, via FileSystemWatcher (no polling). Browsers write
/// to a temporary file (Chrome/Edge ".crdownload", Firefox ".part", …) and rename it when done:
/// partial files are active downloads, and a rename to a normal name is a completed one.
/// </summary>
public sealed partial class DownloadsWatcher : IDownloadSource, IIntegration
{
    private static readonly string[] PartialExtensions = [".crdownload", ".part", ".partial", ".download", ".opdownload"];
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(700);
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DownloadInfo> _active = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private ITimer? _flush;
    private IReadOnlyList<DownloadInfo> _snapshot = [];

    public DownloadsWatcher(TimeProvider time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    public event EventHandler? Changed;

    public event EventHandler<CompletedDownload>? Completed;

    public string Name => "Downloads";

    public bool RequiresNetwork => false;

    public IReadOnlyList<DownloadInfo> Active => Volatile.Read(ref _snapshot);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string folder = DownloadsFolder();
        if (!Directory.Exists(folder))
        {
            AppLog.Info(nameof(DownloadsWatcher), $"No Downloads folder at {folder}");
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            // Downloads already running when WinIsland starts (ignore leftovers from long ago).
            DateTime cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (string path in Directory.EnumerateFiles(folder).Where(IsPartial))
            {
                if (File.GetLastWriteTimeUtc(path) > cutoff)
                {
                    Track(path);
                }
            }

            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += OnCreatedOrChanged;
            _watcher.Changed += OnCreatedOrChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Deleted += OnDeleted;
            _watcher.EnableRaisingEvents = true;
        }

        Publish();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _flush?.Dispose();
            _flush = null;
            _active.Clear();
        }

        Publish();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    internal static bool IsPartial(string path) =>
        PartialExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>"setup.exe.crdownload" → "setup.exe"; Chrome's "Unconfirmed 123.crdownload" → "Download".</summary>
    internal static string DisplayName(string partialPath)
    {
        string name = Path.GetFileNameWithoutExtension(partialPath);
        return name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".com.google.Chrome", StringComparison.OrdinalIgnoreCase)
            ? "Download"
            : name;
    }

    private void OnCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsPartial(e.FullPath))
        {
            return;
        }

        lock (_gate)
        {
            Track(e.FullPath);
        }

        ScheduleFlush();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        bool wasPartial = IsPartial(e.OldFullPath);
        bool isPartial = IsPartial(e.FullPath);
        if (!wasPartial && !isPartial)
        {
            return;
        }

        lock (_gate)
        {
            _active.Remove(e.OldFullPath);
            if (isPartial)
            {
                Track(e.FullPath);
            }
        }

        if (wasPartial && !isPartial)
        {
            RaiseCompleted(e.FullPath);
        }

        Publish();
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsPartial(e.FullPath))
        {
            return;
        }

        bool removed;
        lock (_gate)
        {
            removed = _active.Remove(e.FullPath);
        }

        // Some browsers delete the partial file after copying it to its final name.
        string final = Path.Combine(Path.GetDirectoryName(e.FullPath)!, Path.GetFileNameWithoutExtension(e.FullPath));
        if (removed && File.Exists(final) && new FileInfo(final).Length > 0)
        {
            RaiseCompleted(final);
        }

        Publish();
    }

    // Caller holds _gate.
    private void Track(string path)
    {
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            bytes = 0;
        }

        _active[path] = new DownloadInfo(path, DisplayName(path), bytes, _time.GetUtcNow());
    }

    private void RaiseCompleted(string path)
    {
        long bytes = 0;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (IOException)
        {
        }

        Completed?.Invoke(this, new CompletedDownload(path, Path.GetFileName(path), bytes, _time.GetUtcNow()));
    }

    /// <summary>Size changes arrive many times a second; publish at most every 700 ms.</summary>
    private void ScheduleFlush()
    {
        lock (_gate)
        {
            _flush ??= _time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    _flush?.Dispose();
                    _flush = null;
                }

                Publish();
            }, null, Coalesce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Publish()
    {
        List<DownloadInfo> snapshot;
        lock (_gate)
        {
            snapshot = [.. _active.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
        }

        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string DownloadsFolder()
    {
        if (SHGetKnownFolderPath(DownloadsFolderId, 0, 0, out nint path) == 0 && path != 0)
        {
            try
            {
                return Marshal.PtrToStringUni(path) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);
}
