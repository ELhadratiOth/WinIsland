using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;
using WinIsland.Core.Media;

namespace WinIsland.Platform.Windows.Browser;

/// <summary>
/// Local endpoint (127.0.0.1 only) for the WinIsland browser extension. The extension reports the
/// YouTube / YouTube Music page it sees (<c>POST /state</c>) and long-polls for a like or dislike
/// to press (<c>GET /poll</c>). Only requests that come from a browser extension are answered;
/// ordinary web pages (which always send their own Origin) are refused.
/// </summary>
public sealed class BrowserBridge : IBrowserReactions, IIntegration
{
    public const int Port = 43822;

    private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectedFor = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StateFreshFor = TimeSpan.FromSeconds(90);
    private const int MaxRequest = 16 * 1024;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Queue<BrowserReaction> _commands = new();
    private TaskCompletionSource? _commandReady;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private ITimer? _expiry;
    private BrowserReactionState? _state;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;

    public BrowserBridge(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public event EventHandler? Changed;

    public string Name => "Browser extension bridge";

    public bool RequiresNetwork => false;

    public BrowserReactionState? Current
    {
        get
        {
            lock (_gate)
            {
                return _state is { } s && _time.GetUtcNow() - s.At < StateFreshFor ? s : null;
            }
        }
    }

    public bool IsExtensionConnected
    {
        get
        {
            lock (_gate)
            {
                return _time.GetUtcNow() - _lastPoll < ConnectedFor;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        _listener = listener;
        _ = AcceptLoopAsync(listener, _cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _listener?.Stop();
        _listener = null;
        lock (_gate)
        {
            _expiry?.Dispose();
            _expiry = null;
            _commandReady?.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public Task<bool> SendAsync(BrowserReaction reaction, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!IsExtensionConnected)
            {
                return Task.FromResult(false);
            }

            _commands.Enqueue(reaction);
            _commandReady?.TrySetResult();
            return Task.FromResult(true);
        }
    }

    public Task<bool> LooksLikeYouTubeAsync(string title, CancellationToken cancellationToken) => Task.Run(
        () => BrowserWindowTitles.Read().Any(t =>
            t.Contains("YouTube", StringComparison.OrdinalIgnoreCase) && BrowserReactionProtocol.TitlesMatch(t, title)),
        cancellationToken);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client, token);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(PollWait + TimeSpan.FromSeconds(10));
                NetworkStream stream = client.GetStream();
                HttpRequest? request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                (int status, string body) = await RouteAsync(request, timeout.Token).ConfigureAwait(false);
                await WriteResponseAsync(stream, status, body, request.Origin, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Browser went away mid-request.
            }
        }
    }

    private async Task<(int Status, string Body)> RouteAsync(HttpRequest request, CancellationToken token)
    {
        if (!IsExtensionOrigin(request.Origin))
        {
            return (403, "{}");
        }

        if (request.Method == "OPTIONS")
        {
            return (204, string.Empty);
        }

        switch ((request.Method, request.Path))
        {
            case ("POST", "/state"):
                Accept(BrowserReactionProtocol.ParseState(request.Body, _time.GetUtcNow()));
                return (200, "{\"ok\":true}");
            case ("POST", "/gone"):
                Accept(null);
                return (200, "{\"ok\":true}");
            case ("GET", "/poll"):
                return (200, await NextCommandAsync(token).ConfigureAwait(false));
            default:
                return (404, "{}");
        }
    }

    internal static bool IsExtensionOrigin(string? origin) =>
        origin is not null && (origin.StartsWith("chrome-extension://", StringComparison.Ordinal) ||
                               origin.StartsWith("moz-extension://", StringComparison.Ordinal));

    private void Accept(BrowserReactionState? state)
    {
        lock (_gate)
        {
            _state = state;
            _expiry?.Dispose();
            _expiry = state is null ? null : _time.CreateTimer(_ => Changed?.Invoke(this, EventArgs.Empty), null, StateFreshFor + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> NextCommandAsync(CancellationToken token)
    {
        bool wasConnected;
        TaskCompletionSource ready;
        lock (_gate)
        {
            wasConnected = IsExtensionConnected;
            _lastPoll = _time.GetUtcNow();
            if (_commands.TryDequeue(out BrowserReaction queued))
            {
                return CommandJson(queued);
            }

            ready = _commandReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (!wasConnected)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        try
        {
            await ready.Task.WaitAsync(PollWait, _time, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        lock (_gate)
        {
            _lastPoll = _time.GetUtcNow();
            return _commands.TryDequeue(out BrowserReaction next) ? CommandJson(next) : "{\"command\":null}";
        }
    }

    private static string CommandJson(BrowserReaction reaction) =>
        reaction == BrowserReaction.Like ? "{\"command\":\"like\"}" : "{\"command\":\"dislike\"}";

    // ---- Minimal HTTP/1.1 ----

    private sealed record HttpRequest(string Method, string Path, string? Origin, string Body);

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxRequest];
        int total = 0;
        int headerEnd = -1;
        while (headerEnd < 0 && total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            total += read;
            headerEnd = Encoding.ASCII.GetString(buffer, 0, total).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }

        if (headerEnd < 0)
        {
            return null;
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 2)
        {
            return null;
        }

        string? origin = null;
        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                origin = value;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, out contentLength);
            }
        }

        int bodyStart = headerEnd + 4;
        if (contentLength < 0 || bodyStart + contentLength > buffer.Length)
        {
            return null;
        }

        while (total < bodyStart + contentLength)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, bodyStart + contentLength - total), token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            total += read;
        }

        string path = first[1].Split('?')[0];
        return new HttpRequest(first[0].ToUpperInvariant(), path, origin, Encoding.UTF8.GetString(buffer, bodyStart, contentLength));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, string? origin, CancellationToken token)
    {
        string reason = status switch { 200 => "OK", 204 => "No Content", 403 => "Forbidden", _ => "Not Found" };
        byte[] payload = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        if (IsExtensionOrigin(origin))
        {
            head.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n")
                .Append("Access-Control-Allow-Headers: content-type, x-winisland\r\n")
                .Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }
}
