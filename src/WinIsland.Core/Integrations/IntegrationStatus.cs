namespace WinIsland.Core.Integrations;

public enum IntegrationStatus
{
    Pending,
    Starting,
    Running,

    /// <summary>Paused because the machine is offline; resumes automatically when connectivity returns.</summary>
    WaitingForNetwork,

    /// <summary>Failed; a retry is scheduled with exponential backoff.</summary>
    Faulted,

    Stopped,
}

public sealed record IntegrationStatusChange(string Name, IntegrationStatus Status, string? Error);
