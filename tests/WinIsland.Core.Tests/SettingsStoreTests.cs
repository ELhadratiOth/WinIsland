using WinIsland.Core.Display;
using WinIsland.Core.Settings;
using WinIsland.Core.Visibility;

namespace WinIsland.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "winisland-tests", Guid.NewGuid().ToString("N"), "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
        catch (IOException)
        {
            // Includes DirectoryNotFoundException when nothing was written.
        }
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var store = new SettingsStore(_path);

        IslandSettings settings = store.Load();

        Assert.Equal(VisibilityMode.HideInFullscreen, settings.VisibilityMode);
        Assert.True(settings.HoverToInteract);
    }

    [Fact]
    public async Task Round_trips()
    {
        using var store = new SettingsStore(_path);
        var settings = new IslandSettings
        {
            VisibilityMode = VisibilityMode.HideWhenMaximized,
            MonitorPreference = MonitorPreference.FollowActiveWindow,
            GameProcesses = ["game.exe"],
        };

        await store.SaveAsync(settings, TestContext.Current.CancellationToken);
        IslandSettings loaded = store.Load();

        Assert.Equal(VisibilityMode.HideWhenMaximized, loaded.VisibilityMode);
        Assert.Equal(MonitorPreference.FollowActiveWindow, loaded.MonitorPreference);
        Assert.Equal(["game.exe"], loaded.GameProcesses);
        Assert.Contains("\"HideWhenMaximized\"", await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Corrupt_file_never_blocks_startup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");
        using var store = new SettingsStore(_path);

        Assert.Equal(new IslandSettings().VisibilityMode, store.Load().VisibilityMode);
    }
}
