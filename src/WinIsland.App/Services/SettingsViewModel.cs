using WinIsland.Core.Diagnostics;
using WinIsland.Core.Display;
using WinIsland.Core.Modules;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Settings;
using WinIsland.Core.Visibility;
using WinIsland.Platform.Windows.Media;
using WinIsland.Platform.Windows.Shell;

namespace WinIsland.App.Services;

/// <summary>Backs the Settings window; every change is applied and saved immediately.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly Func<IslandSettings> _get;
    private readonly Action<Func<IslandSettings, IslandSettings>> _update;
    private readonly SpotifyLibrary? _spotify;
    private string _spotifyStatus = string.Empty;

    internal SettingsViewModel(
        Func<IslandSettings> get,
        Action<Func<IslandSettings, IslandSettings>> update,
        IEnumerable<IslandModule> modules,
        SpotifyLibrary? spotify)
    {
        _get = get;
        _update = update;
        _spotify = spotify;
        Modules = [.. modules.Where(m => m.Id != ClockModule.ModuleId).Select(m => new ModuleToggle(m, this))];
        ConnectSpotifyCommand = new AsyncRelayCommand(_ => ConnectSpotifyAsync(), () => _spotify is not null, ex => SpotifyStatus = ex.Message);
        DisconnectSpotifyCommand = new RelayCommand(() =>
        {
            _spotify?.Disconnect();
            RefreshSpotify();
        });

        if (_spotify is not null)
        {
            _spotify.ConnectionChanged += (_, _) => RefreshSpotify();
        }

        RefreshSpotify();
    }

    private IslandSettings S => _get();

    // ---- General ----

    public int VisibilityIndex
    {
        get => (int)S.VisibilityMode;
        set => Update(s => s with { VisibilityMode = (VisibilityMode)Math.Clamp(value, 0, 3) });
    }

    public int DisplayIndex
    {
        get => (int)S.MonitorPreference;
        set => Update(s => s with { MonitorPreference = (MonitorPreference)Math.Clamp(value, 0, 1) });
    }

    public bool HoverToInteract
    {
        get => S.HoverToInteract;
        set => Update(s => s with { HoverToInteract = value });
    }

    public bool FocusMode
    {
        get => S.FocusMode;
        set => Update(s => s with { FocusMode = value });
    }

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            StartupRegistration.SetEnabled(value);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ModuleToggle> Modules { get; }

    // ---- Music ----

    public bool OnlineArtwork
    {
        get => S.OnlineArtworkLookup;
        set => Update(s => s with { OnlineArtworkLookup = value });
    }

    public bool OnlineLyrics
    {
        get => S.OnlineLyrics;
        set => Update(s => s with { OnlineLyrics = value });
    }

    public string SpotifyClientId
    {
        get => S.SpotifyClientId;
        set => Update(s => s with { SpotifyClientId = value?.Trim() ?? string.Empty });
    }

    public bool IsSpotifyConnected => _spotify?.IsConnected ?? false;

    public bool IsSpotifyDisconnected => !IsSpotifyConnected;

    public string SpotifyStatus
    {
        get => _spotifyStatus;
        private set => SetProperty(ref _spotifyStatus, value);
    }

    public AsyncRelayCommand ConnectSpotifyCommand { get; }

    public RelayCommand DisconnectSpotifyCommand { get; }

    /// <summary>Re-reads values changed elsewhere (tray menu, island chips).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    internal void SetModuleEnabled(string id, bool enabled) => Update(s => s with
    {
        DisabledModules = enabled
            ? [.. s.DisabledModules.Where(m => !string.Equals(m, id, StringComparison.OrdinalIgnoreCase))]
            : [.. s.DisabledModules.Append(id).Distinct(StringComparer.OrdinalIgnoreCase)],
    });

    internal bool IsModuleEnabled(string id) => !S.DisabledModules.Contains(id, StringComparer.OrdinalIgnoreCase);

    private void Update(Func<IslandSettings, IslandSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        _update(change);
        OnPropertyChanged(property);
    }

    private async Task ConnectSpotifyAsync()
    {
        SpotifyStatus = "Waiting for you to approve in the browser…";
        await _spotify!.ConnectAsync(CancellationToken.None).ConfigureAwait(true);
        RefreshSpotify();
    }

    private void RefreshSpotify()
    {
        OnPropertyChanged(nameof(IsSpotifyConnected));
        OnPropertyChanged(nameof(IsSpotifyDisconnected));
        SpotifyStatus = IsSpotifyConnected ? "Connected. The heart in the player saves songs to your Liked Songs." : "Not connected.";
        AppLog.Info(nameof(SettingsViewModel), $"Spotify connected: {IsSpotifyConnected}");
    }
}

/// <summary>One module's on/off switch.</summary>
public sealed class ModuleToggle(IslandModule module, SettingsViewModel owner) : ObservableObject
{
    public string Name => module.DisplayName;

    public string Glyph => module.Glyph;

    public bool IsEnabled
    {
        get => owner.IsModuleEnabled(module.Id);
        set
        {
            owner.SetModuleEnabled(module.Id, value);
            OnPropertyChanged();
        }
    }
}
