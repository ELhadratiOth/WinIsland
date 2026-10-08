using WinIsland.Core.Claude;
using WinIsland.Core.GitHub;

namespace WinIsland.App.Services.Preview;

internal sealed class PreviewHookServer : IClaudeHookServer
{
    public event EventHandler<ApprovalRequest>? PermissionRequested;

    public event EventHandler<ClaudeNotice>? NoticeReceived
    {
        add { }
        remove { }
    }

    public ApprovalRequest Request(string tool, string summary, string project)
    {
        var request = new ApprovalRequest(tool, summary, project, null);
        PermissionRequested?.Invoke(this, request);
        return request;
    }
}

internal sealed class PreviewCi : ICiSource
{
    public IReadOnlyList<WorkflowRun> Runs { get; private set; } = [];

    public event EventHandler? Changed;

    public void Set(params WorkflowRun[] runs)
    {
        Runs = runs;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
