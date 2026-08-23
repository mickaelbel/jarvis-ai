using System.Net;
using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _responderWithToken;

    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responderWithToken = responder;
        _responder = req => _responderWithToken(req, CancellationToken.None);
    }

    public static StubHttpHandler For(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    public static StubHttpHandler ForJson(object payload, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload)) }));

    public static StubHttpHandler RespondByUrl(Func<string, string> responderByUrl)
        => new(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responderByUrl(req.RequestUri!.ToString())) }));

    public static StubHttpHandler Status(HttpStatusCode status)
        => new(_ => Task.FromResult(new HttpResponseMessage(status)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10; i++)
        {
            Requests.Add(request);
            var response = _responderWithToken is not null
                ? await _responderWithToken(request, cancellationToken)
                : await _responder(request);

            if ((int)response.StatusCode is >= 300 and < 400 &&
                response.Headers.Location is { } location &&
                (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head))
            {
                request.Dispose();
                request = new HttpRequestMessage(request.Method, location);
                continue;
            }

            return response;
        }

        throw new InvalidOperationException("Too many redirects");
    }
}

public sealed class FakeLinkVerifier : ILinkVerifier
{
    public bool Valid { get; set; } = true;
    public HashSet<string> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Calls { get; private set; }

    public Task<LinkVerificationResult> VerifyAsync(string url, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Rejected.Contains(url))
            return Task.FromResult(new LinkVerificationResult { IsValid = false, Error = "rejected" });
        return Task.FromResult(new LinkVerificationResult { IsValid = Valid, Status = HttpStatusCode.OK, FinalUrl = url });
    }
}

public sealed class StubSearchProvider : IWebSearchProvider
{
    private readonly IReadOnlyList<SearchResult> _results;
    public string Name { get; }
    public bool Handle { get; set; } = true;
    public int Calls { get; private set; }
    public Exception? Throw { get; set; }

    public StubSearchProvider(string name, params SearchResult[] results)
    {
        Name = name;
        _results = results;
    }

    public bool CanHandle(SearchRequest request) => Handle;

    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(_results);
    }
}

public static class TestResults
{
    public static SearchResult Result(string title, string url, string provider = "stub", double? extra = null, string? author = null, DateTimeOffset? published = null)
        => new()
        {
            Title = title,
            Url = url,
            Snippet = "snippet",
            Source = url.Contains("://") ? new Uri(url).Host : "unknown",
            Provider = provider,
            ExtraScore = extra,
            Author = author,
            PublishedAt = published
        };
}
