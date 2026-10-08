using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinIsland.Core.Claude;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;

namespace WinIsland.Platform.Windows.Claude;

/// <summary>Named pipe shared by the island (server) and <c>WinIsland.exe --claude-hook</c> (client).</summary>
internal static class ClaudeHookPipe
{
    /// <summary>Per-user name; the pipe is also restricted to the current user.</summary>
    public static string Name => "WinIsland.ClaudeHook." + Environment.UserName.Replace('\\', '_');

    /// <summary>Claude Code waits up to the hook timeout (300 s); answer "ask" a bit before that.</summary>
    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(280);
}

/// <summary>
/// Receives Claude Code hook calls forwarded by <see cref="ClaudeHookClient"/>. Each connection
/// carries one JSON line {"kind":"permission"|"notify","payload":&lt;hook stdin&gt;}; permission
/// calls are answered with one line {"decision":"allow"|"deny"|"ask"}.
/// </summary>
public sealed class ClaudeHookServer : IClaudeHookServer, IIntegration
{
    private CancellationTokenSource? _cts;

    public event EventHandler<ApprovalRequest>? PermissionRequested;

    public event EventHandler<ClaudeNotice>? NoticeReceived;

    public event EventHandler<ClaudeRateLimits>? RateLimitsReceived;

    public string Name => "Claude Code approvals";

    public bool RequiresNetwork => false;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    ClaudeHookPipe.Name,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                AppLog.Warn(nameof(ClaudeHookServer), "Pipe unavailable", ex);
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                continue;
            }

            _ = HandleAsync(pipe, token);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                JsonNode? envelope = JsonNode.Parse(line);
                string kind = envelope?["kind"]?.GetValue<string>() ?? string.Empty;
                string payload = envelope?["payload"]?.ToJsonString() ?? "{}";
                if (kind == "statusline")
                {
                    if (ClaudeStatusLine.ParseLimits(payload) is { } limits)
                    {
                        RateLimitsReceived?.Invoke(this, limits);
                    }

                    return;
                }

                if (kind == "notify")
                {
                    if (ClaudeHookPayload.ParseNotice(payload) is { } notice)
                    {
                        NoticeReceived?.Invoke(this, notice);
                    }

                    return;
                }

                if (kind != "permission" || ClaudeHookPayload.ParsePermission(payload) is not { } request)
                {
                    return;
                }

                PermissionRequested?.Invoke(this, request);

                // The hook process going away (answered in the terminal, Claude stopped) closes the pipe.
                Task disconnected = reader.ReadLineAsync(token).AsTask();
                Task timeout = Task.Delay(ClaudeHookPipe.DecisionTimeout, token);
                Task first = await Task.WhenAny(request.Decision, disconnected, timeout).ConfigureAwait(false);
                if (first != request.Decision)
                {
                    request.Resolve(ApprovalDecision.Ask);
                    if (first == disconnected)
                    {
                        return;
                    }
                }

                string decision = request.Decision.Result switch
                {
                    ApprovalDecision.Allow => "allow",
                    ApprovalDecision.Deny => "deny",
                    _ => "ask",
                };
                byte[] reply = Encoding.UTF8.GetBytes(new JsonObject { ["decision"] = decision }.ToJsonString() + "\n");
                await pipe.WriteAsync(reply, token).ConfigureAwait(false);
                await pipe.FlushAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException)
            {
                AppLog.Info(nameof(ClaudeHookServer), $"Hook connection ended: {ex.GetType().Name}");
            }
        }
    }
}

/// <summary>
/// <c>WinIsland.exe --claude-hook permission|notify</c>: run by Claude Code as a hook. Forwards
/// the hook input to the running island and prints its decision. When the island isn't
/// running (or anything fails) it prints nothing, so Claude Code falls back to its usual prompt.
/// </summary>
public static class ClaudeHookClient
{
    public static int Run(string kind, TextReader input, TextWriter output)
    {
        try
        {
            string payload = input.ReadToEnd();
            JsonNode? parsed = JsonNode.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);

            // Status line: always print the line Claude Code shows, then hand the limits to the
            // island if it is running (never wait for it).
            if (kind == "statusline")
            {
                output.Write(ClaudeStatusLine.Render(payload));
                output.Flush();
            }

            using var pipe = new NamedPipeClientStream(".", ClaudeHookPipe.Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(kind == "statusline" ? 150 : 500);

            byte[] request = Encoding.UTF8.GetBytes(new JsonObject { ["kind"] = kind, ["payload"] = parsed }.ToJsonString() + "\n");
            pipe.Write(request);
            pipe.Flush();
            if (kind != "permission")
            {
                return 0;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            string? line = reader.ReadLine();
            string decision = line is null ? "ask" : JsonNode.Parse(line)?["decision"]?.GetValue<string>() ?? "ask";
            string response = ClaudeHookPayload.BuildResponse(decision switch
            {
                "allow" => ApprovalDecision.Allow,
                "deny" => ApprovalDecision.Deny,
                _ => ApprovalDecision.Ask,
            });

            if (response.Length > 0)
            {
                output.Write(response);
                output.Flush();
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or UnauthorizedAccessException)
        {
            // The island isn't running or can't be reached: let Claude Code ask as usual.
        }

        return 0;
    }
}
