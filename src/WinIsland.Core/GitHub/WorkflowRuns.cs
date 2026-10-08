using System.Globalization;
using System.Text.Json;

namespace WinIsland.Core.GitHub;

public enum RunState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Other,
}

/// <summary>One GitHub Actions workflow run.</summary>
public sealed record WorkflowRun(long Id, string Repo, string Workflow, string Branch, string Title, RunState State, string Url, DateTimeOffset UpdatedAt)
{
    public bool IsActive => State is RunState.Queued or RunState.Running;
}

/// <summary>Recent workflow runs of the repositories the user follows.</summary>
public interface ICiSource
{
    IReadOnlyList<WorkflowRun> Runs { get; }

    /// <summary>Raised on any thread.</summary>
    event EventHandler? Changed;
}

public static class WorkflowRunsParser
{
    /// <summary>Parses GET /repos/{owner}/{repo}/actions/runs.</summary>
    public static IReadOnlyList<WorkflowRun> Parse(string repo, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("workflow_runs", out JsonElement runs) || runs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<WorkflowRun>();
        foreach (JsonElement run in runs.EnumerateArray())
        {
            string status = Str(run, "status");
            string conclusion = Str(run, "conclusion");
            RunState state = status switch
            {
                "queued" or "waiting" or "requested" or "pending" => RunState.Queued,
                "in_progress" => RunState.Running,
                "completed" => conclusion switch
                {
                    "success" => RunState.Succeeded,
                    "failure" or "timed_out" or "startup_failure" => RunState.Failed,
                    "cancelled" => RunState.Cancelled,
                    _ => RunState.Other,
                },
                _ => RunState.Other,
            };

            DateTimeOffset.TryParse(Str(run, "updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset updated);
            result.Add(new WorkflowRun(
                run.TryGetProperty("id", out JsonElement id) ? id.GetInt64() : 0,
                repo,
                Str(run, "name"),
                Str(run, "head_branch"),
                Str(run, "display_title"),
                state,
                Str(run, "html_url"),
                updated));
        }

        return result;
    }

    /// <summary>The newest run of each workflow, newest first.</summary>
    public static IReadOnlyList<WorkflowRun> Latest(IEnumerable<WorkflowRun> runs, int max) =>
        [.. runs.GroupBy(r => (r.Repo, r.Workflow))
            .Select(g => g.OrderByDescending(r => r.UpdatedAt).First())
            .OrderByDescending(r => r.IsActive)
            .ThenByDescending(r => r.UpdatedAt)
            .Take(max)];

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
