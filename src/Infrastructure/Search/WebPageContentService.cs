using System.Net.Http.Headers;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public interface IWebPageContentService
{
    Task<ContentExtractionResult?> ExtractAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class WebPageContentService : IWebPageContentService
{
    private readonly HttpClient _http;
    private readonly ILogger<WebPageContentService> _logger;

    public WebPageContentService(HttpClient http, ILogger<WebPageContentService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ContentExtractionResult?> ExtractAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https"))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; JarvisAI/1.0)");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("[ContentExtract] {Url}: HTTP {(int)response.StatusCode}", url, (int)response.StatusCode);
            return null;
        }

        if (response.Content.Headers.ContentType?.MediaType is { } mediaType &&
            !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return null;

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var extracted = ContentExtractor.Extract(html, maxWords: 300, url);
        _logger.LogDebug("[ContentExtract] {Url}: {Chars} chars extracted", url, extracted?.Text.Length ?? 0);
        return extracted;
    }
}
