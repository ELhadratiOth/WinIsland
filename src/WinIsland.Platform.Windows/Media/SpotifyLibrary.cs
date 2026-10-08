using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Media;
using WinIsland.Platform.Windows.Security;

namespace WinIsland.Platform.Windows.Media;

/// <summary>
/// Spotify "Liked Songs" through the Web API. Sign-in uses PKCE with the user's own client id
/// (no secret) and a one-shot loopback listener on 127.0.0.1; the refresh token is stored with
/// DPAPI. Nothing is sent to Spotify until the user connects.
/// </summary>
public sealed class SpotifyLibrary : IMusicLibrary, IDisposable
{
    private const string SecretName = "spotify";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Func<string?> _clientId;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _refreshToken;
    private string? _accessToken;
    private DateTimeOffset _accessExpires;

    public SpotifyLibrary(Func<string?> clientId)
    {
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _refreshToken = SecretStore.Read(SecretName);
    }

    public event EventHandler? ConnectionChanged;

    public bool IsConnected => _refreshToken is not null && !string.IsNullOrWhiteSpace(_clientId());

    /// <summary>Opens the browser for consent and waits (up to 3 minutes) for the redirect.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        string clientId = _clientId()?.Trim() ?? string.Empty;
        if (clientId.Length == 0)
        {
            throw new InvalidOperationException("Enter your Spotify app's client ID first.");
        }

        string verifier = SpotifyApi.CreateVerifier();
        string state = SpotifyApi.CreateVerifier()[..16];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        var listener = new TcpListener(IPAddress.Loopback, SpotifyApi.RedirectPort);
        listener.Start();
        try
        {
            Process.Start(new ProcessStartInfo(SpotifyApi.BuildAuthorizeUri(clientId, SpotifyApi.Challenge(verifier), state).AbsoluteUri) { UseShellExecute = true });
            string code = await WaitForCodeAsync(listener, state, timeout.Token).ConfigureAwait(false);

            using var request = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = SpotifyApi.RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            });
            await StoreTokensAsync(await Http.PostAsync("https://accounts.spotify.com/api/token", request, timeout.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }

        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _tokenGate.Dispose();

    public void Disconnect()
    {
        _refreshToken = null;
        _accessToken = null;
        SecretStore.Write(SecretName, null);
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string?> FindTrackAsync(string artist, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string json = await SendAsync(HttpMethod.Get, SpotifyApi.BuildSearchUri(artist, title), cancellationToken).ConfigureAwait(false);
        return SpotifyApi.ParseTrackId(json, artist, title);
    }

    public async Task<bool> IsSavedAsync(string trackId, CancellationToken cancellationToken)
    {
        string json = await SendAsync(HttpMethod.Get, new Uri($"https://api.spotify.com/v1/me/tracks/contains?ids={Uri.EscapeDataString(trackId)}"), cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array &&
            document.RootElement.GetArrayLength() > 0 &&
            document.RootElement[0].GetBoolean();
    }

    public Task SetSavedAsync(string trackId, bool saved, CancellationToken cancellationToken) =>
        SendAsync(saved ? HttpMethod.Put : HttpMethod.Delete, new Uri($"https://api.spotify.com/v1/me/tracks?ids={Uri.EscapeDataString(trackId)}"), cancellationToken);

    private static async Task<string> WaitForCodeAsync(TcpListener listener, string state, CancellationToken cancellationToken)
    {
        while (true)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            // "GET /callback?code=…&state=… HTTP/1.1"; anything else (favicon…) gets a 404.
            string target = requestLine?.Split(' ') is [_, var path, ..] ? path : string.Empty;
            Dictionary<string, string> query = ParseQuery(target);
            bool isCallback = target.StartsWith("/callback", StringComparison.Ordinal);
            bool ok = isCallback && query.TryGetValue("state", out string? s) && s == state && query.ContainsKey("code");

            string body = ok
                ? "<html><body style='font-family:Segoe UI;background:#000;color:#f5f5f7;text-align:center;padding-top:20vh'><h2>WinIsland is connected to Spotify</h2><p>You can close this tab.</p></body></html>"
                : "<html><body style='font-family:Segoe UI'>Spotify sign-in was not completed.</body></html>";
            string status = isCallback ? "200 OK" : "404 Not Found";
            byte[] response = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);

            if (ok)
            {
                return query["code"];
            }

            if (isCallback)
            {
                throw new InvalidOperationException(query.TryGetValue("error", out string? error) ? $"Spotify: {error}" : "Spotify sign-in failed.");
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int q = target.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return result;
        }

        foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        return result;
    }

    private async Task<string> SendAsync(HttpMethod method, Uri uri, CancellationToken cancellationToken)
    {
        string token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _accessToken = null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpires)
            {
                return _accessToken;
            }

            string refresh = _refreshToken ?? throw new InvalidOperationException("Spotify is not connected.");
            using var request = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = _clientId()?.Trim() ?? string.Empty,
            });
            HttpResponseMessage response = await Http.PostAsync("https://accounts.spotify.com/api/token", request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                // Revoked or expired consent: forget it so the UI offers to reconnect.
                AppLog.Warn(nameof(SpotifyLibrary), "Spotify refused the refresh token; disconnecting");
                response.Dispose();
                Disconnect();
                throw new InvalidOperationException("Spotify sign-in expired.");
            }

            await StoreTokensAsync(response).ConfigureAwait(false);
            return _accessToken!;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task StoreTokensAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            JsonElement root = document.RootElement;
            _accessToken = root.GetProperty("access_token").GetString();
            _accessExpires = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out JsonElement e) ? e.GetInt32() - 60 : 3000);
            if (root.TryGetProperty("refresh_token", out JsonElement refresh) && refresh.GetString() is { Length: > 0 } value)
            {
                _refreshToken = value;
                SecretStore.Write(SecretName, value);
            }
        }
    }
}
