using System.Net;
using System.Net.Http.Headers;
using WinIsland.Core.Claude;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;

namespace WinIsland.Platform.Windows.Claude;

/// <summary>
/// The plan limits <c>/usage</c> shows (5-hour and weekly percentages), asked from Claude's own
/// usage endpoint with the sign-in Claude Code keeps in <c>.credentials.json</c> — the same request
/// Claude Code makes. Works without a running Claude Code session and for the whole account.
/// </summary>
/// <remarks>
/// Read-only: the token is only sent to api.anthropic.com, is never stored or logged, and is never
/// refreshed here (refreshing would rotate Claude Code's own token). When it has lapsed, Claude
/// Code renews it the next time it runs. The endpoint is undocumented and rate-limited, so it is
/// asked at most once a minute, every few minutes, and backs off on errors.
/// </remarks>
public sealed class ClaudeLimitsClient : IIntegration
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = CreateClient();

    private readonly Func<bool> _enabled;
    private readonly Func<IReadOnlyList<string>> _credentialFiles;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _wake;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public ClaudeLimitsClient(Func<bool> enabled, TimeProvider time, Func<IReadOnlyList<string>>? credentialFiles = null)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _credentialFiles = credentialFiles ?? (() => ClaudePaths.CredentialFiles(ClaudePaths.CurrentConfigRoots()));
    }

    /// <summary>Raised on a background thread with fresh limits.</summary>
    public event EventHandler<ClaudeRateLimits>? Updated;

    public string Name => "Claude plan limits";

    public bool RequiresNetwork => true;

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

    /// <summary>Asks again soon (the Claude panel was opened, settings changed); still limited to once a minute.</summary>
    public void Refresh() => _wake?.Cancel();

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_enabled())
            {
                await FetchAsync(token).ConfigureAwait(false);
            }

            DateTimeOffset now = _time.GetUtcNow();
            TimeSpan wait = _enabled() ? Interval : Timeout.InfiniteTimeSpan;
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            _wake = wake;
            try
            {
                await Task.Delay(wait, _time, wake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Woken by Refresh(): respect the minimum gap.
                TimeSpan gap = _lastAttempt + MinimumGap - _time.GetUtcNow();
                if (gap > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(gap, _time, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
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

    private async Task FetchAsync(CancellationToken token)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now < _blockedUntil)
        {
            return;
        }

        _lastAttempt = now;
        ClaudeCredentials? credentials = ReadCredentials();
        if (credentials is null)
        {
            return;
        }

        if (credentials.IsExpired(now))
        {
            AppLog.Info(nameof(ClaudeLimitsClient), "Claude sign-in has lapsed; it renews the next time Claude Code runs");
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ClaudePlanUsage.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _blockedUntil = now + (response.Headers.RetryAfter?.Delta is { } d && d > Backoff ? d : Backoff);
                AppLog.Info(nameof(ClaudeLimitsClient), "Usage endpoint is rate limiting; pausing");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Info(nameof(ClaudeLimitsClient), $"Usage endpoint answered HTTP {(int)response.StatusCode}");
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _blockedUntil = now + Backoff;
                }

                return;
            }

            if (ClaudePlanUsage.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)) is { } limits)
            {
                Updated?.Invoke(this, limits);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                AppLog.Info(nameof(ClaudeLimitsClient), $"Usage request failed: {ex.GetType().Name}");
            }
        }
    }

    private ClaudeCredentials? ReadCredentials()
    {
        foreach (string file in _credentialFiles())
        {
            try
            {
                if (ClaudeCredentials.Parse(File.ReadAllText(file)) is { } credentials)
                {
                    return credentials;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Info(nameof(ClaudeLimitsClient), $"Could not read Claude credentials: {ex.GetType().Name}");
            }
        }

        return null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/0.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
