namespace WinIsland.Core.Claude;

/// <summary>Sends a follow-up prompt to an existing Claude Code session.</summary>
public interface IClaudeMessenger
{
    bool IsAvailable { get; }

    Task SendAsync(ClaudeSessionInfo session, string message, CancellationToken cancellationToken);
}
