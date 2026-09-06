using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using JarvisAI.Application.AI;
using JarvisAI.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class OllamaKeepAliveServiceTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var model = doc.RootElement.TryGetProperty("model", out var prop)
                ? prop.GetString() ?? string.Empty
                : string.Empty;

            Calls.AddOrUpdate(model, 1, (_, count) => count + 1);
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"done\":true}") };
        }
    }

    private static ModelRouter CreateRouter()
        => new(new ModelRouterOptions(FastModel: "llama3.1:latest", ReasoningModel: "llama3.3"), NullLogger<ModelRouter>.Instance);

    [Fact]
    public async Task Preloads_router_and_extra_models()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var service = new OllamaKeepAliveService(
            http,
            CreateRouter(),
            NullLogger<OllamaKeepAliveService>.Instance,
            new[] { "nomic-embed-text", "llava" });

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (handler.Calls.Count < 4 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Contains("llama3.1:latest", handler.Calls.Keys);
            Assert.Contains("llama3.3", handler.Calls.Keys);
            Assert.Contains("nomic-embed-text", handler.Calls.Keys);
            Assert.Contains("llava", handler.Calls.Keys);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            http.Dispose();
        }
    }

    [Fact]
    public async Task Missing_models_are_retried_but_loaded_ones_are_not_hit_in_backoff_loop()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.NotFound };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var service = new OllamaKeepAliveService(
            http,
            CreateRouter(),
            NullLogger<OllamaKeepAliveService>.Instance,
            new[] { "nomic-embed-text" });

        await service.StartAsync(CancellationToken.None);
        try
        {
            // All models missing -> service keeps retrying the pending set, never stalls
            await Task.Delay(350);
            Assert.Contains("llama3.1:latest", handler.Calls.Keys);
            Assert.Contains("nomic-embed-text", handler.Calls.Keys);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            http.Dispose();
        }
    }
}
