using WinIsland.Core.Claude;
using WinIsland.Core.Mvvm;

namespace WinIsland.Core.Modules;

/// <summary>A row in the Claude Code panel. Reused across updates so the list never flickers.</summary>
public sealed class ClaudeSessionItem : ObservableObject
{
    // Muted, distinct avatar tints that read well on black.
    private static readonly uint[] AvatarPalette =
    [
        0xFF5E9EFF, 0xFFBF8CFF, 0xFFFF8C8C, 0xFF5FD6A4, 0xFFFFC266, 0xFF6ED3E8, 0xFFFF9AD5, 0xFFA8B4FF,
    ];

    private ClaudeSessionInfo _info;
    private string _detail = string.Empty;

    public ClaudeSessionItem(ClaudeSessionInfo info, DateTimeOffset now)
    {
        _info = info;
        _detail = Describe(info, now);
    }

    public ClaudeSessionInfo Info => _info;

    public string SessionId => _info.SessionId;

    public string ProjectName => _info.ProjectName;

    public bool IsActive => _info.IsActive;

    /// <summary>One or two letters for the avatar, e.g. "BA" for backend-api.</summary>
    public string Initials => MakeInitials(_info.ProjectName);

    /// <summary>Stable per-project avatar colour (ARGB).</summary>
    public uint AvatarColor => AvatarPalette[StableHash(_info.ProjectPath ?? _info.ProjectName) % (uint)AvatarPalette.Length];

    /// <summary>"Working · now", "Idle · 5m"…</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public void Update(ClaudeSessionInfo info, DateTimeOffset now)
    {
        bool changed = info != _info;
        _info = info;
        Detail = Describe(info, now);
        if (changed)
        {
            OnPropertyChanged(nameof(Info));
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(Initials));
            OnPropertyChanged(nameof(AvatarColor));
        }
    }

    internal static string MakeInitials(string name)
    {
        string[] parts = name.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}",
        };
    }

    internal static string Describe(ClaudeSessionInfo info, DateTimeOffset now)
    {
        TimeSpan ago = now - info.LastActivity;
        string when = ago.TotalMinutes < 1 ? "now"
            : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes}m"
            : ago.TotalDays < 1 ? $"{(int)ago.TotalHours}h"
            : $"{(int)ago.TotalDays}d";
        return $"{(info.IsActive ? "Working" : "Idle")} · {when}";
    }

    private static uint StableHash(string text)
    {
        // FNV-1a: stable across runs, unlike string.GetHashCode.
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }
}
