using System.Net;
using JarvisAI.Application.Search;

namespace JarvisAI.Tests;

public sealed class LinkVerifierTests
{
    private static LinkVerifier Create(StubHttpHandler handler)
        => new(new HttpClient(handler));

    [Fact]
    public async Task Valid_2xx_head_is_verified()
    {
        var handler = StubHttpHandler.Status(HttpStatusCode.OK);
        var verifier = Create(handler);

        var result = await verifier.VerifyAsync("https://example.com");

        Assert.True(result.IsValid);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(HttpMethod.Head, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Created_status_is_valid()
    {
        var handler = StubHttpHandler.Status(HttpStatusCode.Created);
        var verifier = Create(handler);

        var result = await verifier.VerifyAsync("https://example.com");

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Rejects_error_statuses(HttpStatusCode status)
    {
        var handler = StubHttpHandler.Status(status);
        var verifier = Create(handler);

        var result = await verifier.VerifyAsync("https://example.com");

        Assert.False(result.IsValid);
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task Falls_back_to_get_when_head_not_supported()
    {
        var handler = new StubHttpHandler(req => req.Method == HttpMethod.Head
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var verifier = Create(handler);

        var result = await verifier.VerifyAsync("https://example.com");

        Assert.True(result.IsValid);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Get_fallback_also_rejects_404()
    {
        var handler = new StubHttpHandler(req => req.Method == HttpMethod.Head
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var verifier = Create(handler);

        var result = await verifier.VerifyAsync("https://example.com");

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Network_failure_is_rejected()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var verifier = Create(handler);

        var result = await verifier.VerifyAsync("https://example.com");

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://files.example.com")]
    public async Task Invalid_inputs_are_rejected(string url)
    {
        var handler = StubHttpHandler.Status(HttpStatusCode.OK);
        var verifier = Create(handler);

        var result = await verifier.VerifyAsync(url);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Redirect_is_followed_and_valid()
    {
        var handler = new StubHttpHandler(req =>
            req.RequestUri!.AbsolutePath == "/start"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://example.com/final") },
                    RequestMessage = req
                })
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var verifier = Create(handler);

        var result = await verifier.VerifyAsync("https://example.com/start");

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Timeout_produces_failure_not_exception()
    {
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var verifier = new LinkVerifier(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) });

        var result = await verifier.VerifyAsync("https://example.com");

        Assert.False(result.IsValid);
    }
}
