using System.Net;
using System.Net.Http.Headers;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.GitHub;
using WinIsland.Core.Integrations;

namespace WinIsland.Platform.Windows.GitHub;

/// <summary>
/// GitHub Actions runs for the repositories in settings. GitHub has no push channel for a
/// desktop app, so this asks with ETags (unchanged answers are free and don't count against
/// the rate limit): every 30 s while something builds, every 3 minutes otherwise, and not at
/// all without repositories or while offline.
/// </summary>
public sealed class GitHubActionsClient : ICiSource, IIntegration
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(3);
    private static readonly HttpClient Http = CreateClient();

    private readonly Func<IReadOnlyList<string>> _repos;
    private readonly Func<string?> _token;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (string ETag, IReadOnlyList<WorkflowRun> Runs)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _wake;
    private IReadOnlyList<WorkflowRun> _runs = [];

    public GitHubActionsClient(Func<IReadOnlyList<string>> repos, Func<string?> token, TimeProvider time)
    {
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public event EventHandler? Changed;

    public string Name => "GitHub Actions";

    public bool RequiresNetwork => true;

    public IReadOnlyList<WorkflowRun> Runs => Volatile.Read(ref _runs);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
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

    /// <summary>Settings changed: fetch now instead of waiting for the next interval.</summary>
    public void Refresh() => _wake?.Cancel();

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<string> repos = _repos();
            if (repos.Count > 0)
            {
                await PollAsync(repos, token).ConfigureAwait(false);
            }
            else if (_runs.Count > 0)
            {
                Publish([]);
            }

            TimeSpan wait = repos.Count == 0 ? Timeout.InfiniteTimeSpan : Runs.Any(r => r.IsActive) ? ActiveInterval : IdleInterval;
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            _wake = wake;
            try
            {
                await Task.Delay(wait, _time, wake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Woken by Refresh().
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _wake = null;
            }
        }
    }

    private async Task PollAsync(IReadOnlyList<string> repos, CancellationToken token)
    {
        var all = new List<WorkflowRun>();
        string? accessToken = _token();
        foreach (string repo in repos)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/actions/runs?per_page=10");
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }

                if (_cache.TryGetValue(repo, out var cached))
                {
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag, cached.ETag.StartsWith("W/", StringComparison.Ordinal)));
                }

                using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified && cached.Runs is not null)
                {
                    all.AddRange(cached.Runs);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Info(nameof(GitHubActionsClient), $"{repo}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                IReadOnlyList<WorkflowRun> runs = WorkflowRunsParser.Parse(repo, await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                string? etag = response.Headers.ETag?.ToString();
                if (etag is not null)
                {
                    _cache[repo] = (etag.StartsWith("W/", StringComparison.Ordinal) ? etag[2..] : etag, runs);
                }

                all.AddRange(runs);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                AppLog.Info(nameof(GitHubActionsClient), $"{repo}: {ex.GetType().Name}");
            }
        }

        Publish(all);
    }

    private void Publish(IReadOnlyList<WorkflowRun> runs)
    {
        Volatile.Write(ref _runs, runs);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/0.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
