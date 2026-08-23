using System.Collections.Concurrent;
using System.Net;

namespace JarvisAI.Application.Search;

public interface ILinkVerifier
{
    Task<LinkVerificationResult> VerifyAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class LinkVerificationResult
{
    public bool IsValid { get; init; }
    public HttpStatusCode? Status { get; init; }
    public string? FinalUrl { get; init; }
    public string? Error { get; init; }
}

public sealed class LinkVerifier : ILinkVerifier
{
    private static readonly HttpStatusCode[] HeadUnsupported =
    {
        HttpStatusCode.MethodNotAllowed,
        HttpStatusCode.NotImplemented,
        (HttpStatusCode)501
    };

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, CachedVerification> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private sealed record CachedVerification(LinkVerificationResult Result, DateTime Expiry);

    public LinkVerifier(HttpClient http)
    {
        _http = http;
    }

    public async Task<LinkVerificationResult> VerifyAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new LinkVerificationResult { IsValid = false, Error = "Invalid URL" };
        if (uri.Scheme is not ("http" or "https"))
            return new LinkVerificationResult { IsValid = false, Error = $"Unsupported scheme: {uri.Scheme}" };

        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(url, out var cached) && cached.Expiry > now)
            return cached.Result;

        var result = await VerifyCoreAsync(url, cancellationToken).ConfigureAwait(false);
        _cache[url] = new CachedVerification(result, now.Add(CacheTtl));
        return result;
    }

    private async Task<LinkVerificationResult> VerifyCoreAsync(string url, CancellationToken cancellationToken)
    {
        var status = await TryHeadAsync(url, cancellationToken);
        if (status is { } head)
        {
            if (IsSuccess(status.Value))
                return new LinkVerificationResult { IsValid = true, Status = status, FinalUrl = url };
            if (IsRejection(status.Value))
                return new LinkVerificationResult { IsValid = false, Status = status, Error = $"HTTP {(int)status.Value}" };
        }

        var getResult = await TryGetAsync(url, cancellationToken);
        return getResult;
    }

    private async Task<HttpStatusCode?> TryHeadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (HeadUnsupported.Contains(response.StatusCode))
                return null;
            return response.StatusCode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<LinkVerificationResult> TryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (IsSuccess(response.StatusCode))
                return new LinkVerificationResult { IsValid = true, Status = response.StatusCode, FinalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url };
            return new LinkVerificationResult { IsValid = false, Status = response.StatusCode, Error = $"HTTP {(int)response.StatusCode}" };
        }
        catch (Exception ex)
        {
            return new LinkVerificationResult { IsValid = false, Error = ex.Message };
        }
    }

    private static bool IsSuccess(HttpStatusCode status)
        => (int)status is >= 200 and < 300;

    private static bool IsRejection(HttpStatusCode status)
    {
        var code = (int)status;
        return code is 404 or 403 or 410 or 500 or 502 or 503 or 504 or 400 or 401 or 429;
    }
}
