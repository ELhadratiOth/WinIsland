using WinIsland.Core.Claude;
using WinIsland.Core.Mvvm;

namespace WinIsland.Core.Modules;

/// <summary>A row in the Claude Code panel. Reused across updates so the list never flickers.</summary>
public sealed class ClaudeSessionItem : ObservableObject
{
    private ClaudeSessionInfo _info;

    public ClaudeSessionItem(ClaudeSessionInfo info)
    {
        _info = info;
    }

    public ClaudeSessionInfo Info
    {
        get => _info;
        set
        {
            if (SetProperty(ref _info, value))
            {
                OnPropertyChanged(nameof(ProjectName));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StatusGlyph));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string SessionId => _info.SessionId;

    public string ProjectName => _info.ProjectName;

    public bool IsActive => _info.IsActive;

    /// <summary>Filled / hollow circle.</summary>
    public string StatusGlyph => _info.IsActive ? "●" : "○";

    public string StatusText => _info.IsActive ? "working" : "idle";
}
