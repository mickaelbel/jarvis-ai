using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Integrations;

// Helper OAuth2 Google partagé (refresh token → access token avec cache)
public sealed class GoogleAuthHelper
{
    private readonly IntegrationsStore _store;
    private readonly ILogger<GoogleAuthHelper> _logger;
    private readonly HttpClient _http;
    private string? _cachedAccessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public GoogleAuthHelper(IntegrationsStore store, ILogger<GoogleAuthHelper> logger)
    {
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var settings = _store.Get().Google;
        if (string.IsNullOrWhiteSpace(settings.RefreshToken) ||
            string.IsNullOrWhiteSpace(settings.ClientId) ||
            string.IsNullOrWhiteSpace(settings.ClientSecret))
            throw new InvalidOperationException("Google OAuth non configuré : ClientId, ClientSecret, RefreshToken requis dans les paramètres.");

        if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTime.UtcNow < _tokenExpiry.AddMinutes(-1))
            return _cachedAccessToken;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["refresh_token"] = settings.RefreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(form), ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        _cachedAccessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

        _logger.LogInformation("[GoogleAuth] Nouvel access token obtenu (expire dans {Sec}s)", expiresIn);
        return _cachedAccessToken!;
    }

    public void InvalidateCache() => _cachedAccessToken = null;
}