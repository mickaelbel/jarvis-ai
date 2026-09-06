using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using JarvisAI.Application.AI;
using JarvisAI.Infrastructure.AI;
using JarvisAI.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class OllamaMetricsTests
{
    private sealed class StreamHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
    }

    private static AIRequest CreateRequest(string model = "llama3.1:latest")
        => new(
            systemPrompt: "system",
            messages: new[] { AIMessage.User("hello") },
            model: model);

    [Fact]
    public async Task StreamChatAsync_emits_done_chunk_with_metrics_and_records_monitor()
    {
        var body = string.Join("\n",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Hello \"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"world\"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"prompt_eval_count\":12,\"eval_count\":34,\"eval_duration\":1000000000}") + "\n";

        var monitor = new OllamaRunMonitor();
        var provider = new OllamaProvider(
            new HttpClient(new StreamHandler { Body = body }) { BaseAddress = new Uri("http://localhost:11434") },
            NullLogger<OllamaProvider>.Instance,
            monitor: monitor);

        var tokens = new List<string>();
        AIStreamChunk? done = null;
        await foreach (var chunk in provider.StreamChatAsync(CreateRequest()))
        {
            if (chunk.Done) { done = chunk; continue; }
            if (chunk.Token is not null) tokens.Add(chunk.Token);
        }

        Assert.Equal("Hello world", string.Concat(tokens));
        Assert.NotNull(done);
        Assert.Equal(12, done.PromptTokens);
        Assert.Equal(34, done.CompletionTokens);
        Assert.Equal(1000, done.EvalDurationMs);
        Assert.Equal(34, monitor.Last?.CompletionTokens);
        Assert.Equal("llama3.1:latest", monitor.Last?.Model);
    }

    [Fact]
    public async Task ChatAsync_populates_tokens_and_speed()
    {
        var body = "{\"message\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"done\":true,\"prompt_eval_count\":5,\"eval_count\":7,\"eval_duration\":2000000000}";
        var monitor = new OllamaRunMonitor();
        var provider = new OllamaProvider(
            new HttpClient(new StreamHandler { Body = body }) { BaseAddress = new Uri("http://localhost:11434") },
            NullLogger<OllamaProvider>.Instance,
            monitor: monitor);

        var response = await provider.ChatAsync(CreateRequest());

        Assert.True(response.Success);
        Assert.Equal("Hi", response.Content);
        Assert.Equal(5, response.PromptTokens);
        Assert.Equal(7, response.CompletionTokens);
        Assert.Equal(2000, response.EvalDurationMs);
        Assert.Equal(3.5, response.TokensPerSecond, 1);
        Assert.Equal(7, monitor.Last?.CompletionTokens);
    }

    [Fact]
    public void AIResponse_reports_tokens_per_second()
    {
        var response = AIResponse.Text("content", "m", 10, 20, evalDurationMs: 500);
        Assert.Equal(40, response.TokensPerSecond, 1);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public string ProcessesJson { get; set; } = "";
        public ConcurrentDictionary<string, int> UnloadCalls { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/ps")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ProcessesJson) };

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var model = doc.RootElement.TryGetProperty("model", out var p) ? p.GetString() ?? "" : "";
            var keepAlive = doc.RootElement.TryGetProperty("keep_alive", out var ka) ? ka.GetRawText() : "";
            if (keepAlive == "0")
                UnloadCalls.AddOrUpdate(model, 1, (_, count) => count + 1);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"done\":true}") };
        }
    }

    private static ModelRouter CreateRouter()
        => new(new ModelRouterOptions(FastModel: "llama3.1:latest", ReasoningModel: "llama3.3"), NullLogger<ModelRouter>.Instance);

    private static string ProcessesJson(params string[] models)
        => "{\"models\":[" + string.Join(",", models.Select(m =>
            "{\"name\":\"" + m + "\",\"size\":123456,\"size_vram\":100000,\"expires_at\":\"2026-01-01T00:00:00Z\",\"details\":{\"processor\":\"GPU\",\"parameter_size\":\"8B\",\"quantization_level\":\"Q4_K_M\"}}")) + "]}";

    [Fact]
    public async Task Unloads_models_idle_beyond_timeout_and_keeps_active_ones()
    {
        var handler = new RoutingHandler { ProcessesJson = ProcessesJson("llava:latest", "llama3.1:latest") };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var modelService = new OllamaModelService(http, NullLogger<OllamaModelService>.Instance);

        var service = new OllamaKeepAliveService(
            http,
            CreateRouter(),
            NullLogger<OllamaKeepAliveService>.Instance,
            monitor: new OllamaRunMonitor(),
            modelService: modelService,
            idleUnloadTimeout: TimeSpan.FromMinutes(10));

        var now = DateTimeOffset.UtcNow;
        service.MarkUsed("llama3.1:latest", now.AddMinutes(-30));
        service.MarkUsed("llava:latest", now);

        var unloaded = await service.UnloadIdleModelsAsync(now, CancellationToken.None);

        Assert.Contains("llama3.1:latest", unloaded);
        Assert.DoesNotContain("llava:latest", unloaded);
        Assert.True(handler.UnloadCalls.ContainsKey("llama3.1:latest"));
        Assert.False(handler.UnloadCalls.ContainsKey("llava:latest"));
        http.Dispose();
    }

    [Fact]
    public async Task Does_not_unload_when_disabled()
    {
        var handler = new RoutingHandler { ProcessesJson = ProcessesJson("llama3.1:latest") };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var modelService = new OllamaModelService(http, NullLogger<OllamaModelService>.Instance);

        var service = new OllamaKeepAliveService(
            http,
            CreateRouter(),
            NullLogger<OllamaKeepAliveService>.Instance,
            monitor: new OllamaRunMonitor(),
            modelService: modelService,
            idleUnloadTimeout: TimeSpan.FromMinutes(10),
            idleUnloadEnabled: false);

        service.MarkUsed("llama3.1:latest", DateTimeOffset.UtcNow.AddHours(-2));
        var unloaded = await service.UnloadIdleModelsAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(unloaded);
        Assert.Empty(handler.UnloadCalls);
        http.Dispose();
    }

    [Fact]
    public async Task Does_not_unload_models_with_unknown_activity()
    {
        var handler = new RoutingHandler { ProcessesJson = ProcessesJson("llava:latest") };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var modelService = new OllamaModelService(http, NullLogger<OllamaModelService>.Instance);

        var service = new OllamaKeepAliveService(
            http,
            CreateRouter(),
            NullLogger<OllamaKeepAliveService>.Instance,
            monitor: new OllamaRunMonitor(),
            modelService: modelService,
            idleUnloadTimeout: TimeSpan.FromMinutes(10));

        var unloaded = await service.UnloadIdleModelsAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(unloaded);
        Assert.Empty(handler.UnloadCalls);
        http.Dispose();
    }
}
