using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;
using WinIsland.Core.Personal;

namespace WinIsland.Platform.Windows.Personal;

/// <summary>
/// Calendar subscription feeds (the private "iCal"/"ICS" links Google Calendar, Outlook and
/// iCloud offer). Fetched every 15 minutes while online; links are kept with DPAPI.
/// </summary>
public sealed class IcsCalendarClient : ICalendarSource, IIntegration
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = CreateClient();

    private readonly Func<IReadOnlyList<string>> _urls;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _wake;
    private IReadOnlyList<CalendarEvent> _events = [];

    public IcsCalendarClient(Func<IReadOnlyList<string>> urls, TimeProvider time)
    {
        _urls = urls;
        _time = time;
    }

    public event EventHandler? Changed;

    public string Name => "Calendar";

    public bool RequiresNetwork => true;

    public IReadOnlyList<CalendarEvent> Events => Volatile.Read(ref _events);

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

    public void Refresh() => _wake?.Cancel();

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<string> urls = _urls();
            var all = new List<CalendarEvent>();
            DateTimeOffset now = _time.GetUtcNow();
            foreach (string raw in urls)
            {
                string url = raw.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + raw[9..] : raw;
                try
                {
                    string ics = await Http.GetStringAsync(url, token).ConfigureAwait(false);
                    all.AddRange(IcsParser.Parse(ics, now.AddDays(-1), now.AddDays(2)));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    AppLog.Info(nameof(IcsCalendarClient), $"Calendar feed failed: {ex.GetType().Name}");
                }
            }

            all.Sort((a, b) => a.Start.CompareTo(b.Start));
            Volatile.Write(ref _events, all);
            Changed?.Invoke(this, EventArgs.Empty);

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            _wake = wake;
            try
            {
                await Task.Delay(urls.Count == 0 ? Timeout.InfiniteTimeSpan : Interval, _time, wake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/0.2");
        return client;
    }
}
