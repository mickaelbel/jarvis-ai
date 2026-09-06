using JarvisAI.Application.Vision;
using JarvisAI.Infrastructure.Vision;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace JarvisAI.Tests;

public sealed class OllamaVisionServiceTests
{
    [Fact]
    public async Task DescribeImageAsync_ReturnsContent_WhenModelAvailable()
    {
        var handler = new MockHttpHandler(async request =>
        {
            if (request.RequestUri!.PathAndQuery.StartsWith("/api/tags"))
                return JsonSerializer.Serialize(new { models = new[] { new { name = "llava" } } });

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.True(body.RootElement.TryGetProperty("messages", out var messages));
            var image = messages[0].GetProperty("images")[0].GetString();
            Assert.NotNull(image);

            return JsonSerializer.Serialize(new { message = new { role = "assistant", content = "A screenshot showing a desktop." } });
        });

        var service = new OllamaVisionService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") }, NullLogger<OllamaVisionService>.Instance);
        var imageBytes = new byte[] { 1, 2, 3 };

        var result = await service.DescribeImageAsync(imageBytes);

        Assert.True(result.Success);
        Assert.Contains("desktop", result.Description);
    }

    [Fact]
    public async Task DescribeImageAsync_Fails_WhenNoVisionModel()
    {
        var handler = new MockHttpHandler(_ =>
            Task.FromResult(JsonSerializer.Serialize(new { models = new[] { new { name = "llama3.1:latest" } } })));

        var service = new OllamaVisionService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") }, NullLogger<OllamaVisionService>.Instance);

        var result = await service.DescribeImageAsync(new byte[] { 1, 2, 3 });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DescribeImageAsync_Fails_WhenOllamaUnreachable()
    {
        var handler = new MockHttpHandler(_ => throw new HttpRequestException("connection refused"));

        var service = new OllamaVisionService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") }, NullLogger<OllamaVisionService>.Instance);

        var result = await service.DescribeImageAsync(new byte[] { 1, 2, 3 });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DescribeImageAsync_Fails_OnEmptyImage()
    {
        var handler = new MockHttpHandler(_ => throw new InvalidOperationException("should not be called"));

        var service = new OllamaVisionService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") }, NullLogger<OllamaVisionService>.Instance);

        var result = await service.DescribeImageAsync(Array.Empty<byte>());

        Assert.False(result.Success);
        Assert.Contains("No image data", result.ErrorMessage);
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<string>> _responder;

        public MockHttpHandler(Func<HttpRequestMessage, Task<string>> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = await _responder(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
