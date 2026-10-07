using System.Diagnostics;

namespace WinIsland.Core.Claude;

/// <summary>
/// Resumes a session through the Claude Code CLI in non-interactive mode
/// (<c>claude --resume &lt;id&gt; --print</c>), passing the prompt on standard input so user text is
/// never interpreted as command-line options. Runs entirely in the background.
/// </summary>
public sealed class ClaudeCliMessenger : IClaudeMessenger
{
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(30);
    private readonly Lazy<string?> _executable;

    public ClaudeCliMessenger(string? executablePath = null)
    {
        _executable = new Lazy<string?>(() => executablePath ?? FindExecutable());
    }

    public bool IsAvailable => _executable.Value is not null;

    public async Task SendAsync(ClaudeSessionInfo session, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        string executable = _executable.Value
            ?? throw new InvalidOperationException("The Claude Code CLI (claude.exe) was not found on PATH.");

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = session.ProjectPath is { } path && Directory.Exists(path) ? path : Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("--resume");
        startInfo.ArgumentList.Add(session.SessionId);
        startInfo.ArgumentList.Add("--print");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Claude Code.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaxRunTime);
        try
        {
            await process.StandardInput.WriteAsync(message.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            // Drain both pipes so the child never blocks on a full buffer.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string error = (await stderr.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(error) ? $"Claude Code exited with code {process.ExitCode}." : Tail(error, 300));
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

    private static string? FindExecutable()
    {
        // Only real executables: launching a .cmd shim would route user text through cmd.exe parsing.
        string name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));

        foreach (string directory in directories)
        {
            try
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return null;
    }
}
