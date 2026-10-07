using System.Text.Json;
using System.Text.Json.Serialization;
using WinIsland.Core.Diagnostics;

namespace WinIsland.Core.Settings;

/// <summary>
/// Loads and saves <see cref="IslandSettings"/> as JSON. Loading is synchronous because the
/// file is tiny and the island's first frame depends on it; saving happens off the UI thread.
/// A missing or corrupt file never prevents startup — defaults are used instead.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland", "settings.json");

    public void Dispose() => _saveGate.Dispose();

    public IslandSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new IslandSettings();
            }

            using FileStream stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.IslandSettings) ?? new IslandSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn(nameof(SettingsStore), "Settings could not be read; using defaults", ex);
            return new IslandSettings();
        }
    }

    public async Task SaveAsync(IslandSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SettingsJsonContext.Default.IslandSettings, cancellationToken).ConfigureAwait(false);
            }

            // Atomic replace so a crash mid-write never corrupts the settings.
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(IslandSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
