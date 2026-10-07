namespace WinIsland.Core.Claude;

/// <summary>A local Claude Code session discovered from its transcript file.</summary>
public sealed record ClaudeSessionInfo(
    string SessionId,
    string ProjectName,
    string? ProjectPath,
    string TranscriptPath,
    DateTimeOffset LastActivity,
    bool IsActive);
