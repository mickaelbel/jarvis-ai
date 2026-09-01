using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisAI.Infrastructure.AI;

public sealed class OllamaProvider : IAIProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaProvider> _logger;
    private readonly OllamaRunMonitor _monitor;
    private readonly OllamaLauncher _launcher;
    private readonly string _model;

    private static readonly TimeSpan SuccessCacheWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureCacheWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public string Name => "Ollama";

    public IReadOnlyList<string> KnownModels => Array.Empty<string>();

    public bool MatchesModel(string? model) => false;

    private readonly object _probeGate = new();
    private DateTime _lastProbeUtc = DateTime.MinValue;
    private bool _probeResult;

    public bool IsAvailable
    {
        get
        {
            lock (_probeGate)
            {
                var cacheWindow = _probeResult ? SuccessCacheWindow : FailureCacheWindow;
                if (DateTime.UtcNow - _lastProbeUtc < cacheWindow)
                    return _probeResult;
            }
            // Cache expiré : on rafraîchit en arrière-plan SANS bloquer le thread
            // appelant (sur le circuit Blazor Server, tout appel bloquant gèlerait
            // l'UI). On renvoie le dernier état connu immédiatement.
            _ = RefreshInBackgroundAsync();
            lock (_probeGate) return _probeResult;
        }
    }

    private readonly SemaphoreSlim _probeRefreshGate = new(1, 1);

    private async Task RefreshInBackgroundAsync()
    {
        if (!await _probeRefreshGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var ok = await IsAvailableAsync().ConfigureAwait(false);
            lock (_probeGate)
            {
                _probeResult = ok;
                _lastProbeUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _probeRefreshGate.Release();
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        lock (_probeGate)
        {
            var cacheWindow = _probeResult ? SuccessCacheWindow : FailureCacheWindow;
            if (DateTime.UtcNow - _lastProbeUtc < cacheWindow)
                return _probeResult;
        }

        if (await ProbeOnceAsync(cancellationToken).ConfigureAwait(false))
            return true;

        _logger.LogInformation("[Ollama] Serveur non joignable; tentative de démarrage automatique...");
        try
        {
            await _launcher.EnsureRunningAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ollama] Démarrage automatique échoué");
        }

        // Après l'attente (relance ou déblocage d'un Ollama occupé), on ressonde
        // plusieurs fois : un Ollama qui charge un gros modèle ne répond que tardivement.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (await ProbeOnceAsync(cancellationToken).ConfigureAwait(false)) return true;
            if (attempt < 3) await Task.Delay(2000 * attempt, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<bool> ProbeOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            using var response = await _httpClient.GetAsync("/api/tags", cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Ollama] Availability probe failed: {Error}", ex.Message);
            return false;
        }
    }

    private readonly int _numCtx;

    public OllamaProvider(HttpClient httpClient, ILogger<OllamaProvider> logger, string model = "qwen3:8b", OllamaRunMonitor? monitor = null, OllamaLauncher? launcher = null, int numCtx = 32768)
    {
        _httpClient = httpClient;
        _logger = logger;
        _model = model;
        _monitor = monitor ?? new OllamaRunMonitor();
        _launcher = launcher ?? new OllamaLauncher(Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaLauncher>.Instance);
        _numCtx = numCtx;
    }

    public async Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var requestedModel = string.IsNullOrEmpty(request.Model) ? _model : request.Model;
        var model = requestedModel;

        for (var fallbackAttempt = 0; fallbackAttempt < 2; fallbackAttempt++)
        {
            var messages = BuildOllamaMessages(request);
            var tools = ModelCapabilities.SupportsTools(model)
                ? request.Tools
                : Array.Empty<AIToolDefinition>();

            if (tools.Count > 0)
                _logger.LogInformation("[Ollama] Request has {ToolCount} tools (native tool calling enabled)", tools.Count);

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["options"] = new Dictionary<string, object>
                {
                    ["temperature"] = request.Temperature,
                    ["num_predict"] = request.MaxTokens,
                    ["num_ctx"] = _numCtx
                },
                ["stream"] = false,
                ["keep_alive"] = "30m"
            };

            if (tools.Count > 0)
                payload["tools"] = BuildOllamaTools(tools);

            var jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
            _logger.LogDebug("[Ollama] Sending request to {Model}\n{Payload}", model, jsonPayload);

            const int maxAttempts = 3;
            Exception? lastException = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var httpResponse = await _httpClient.PostAsync("/api/chat",
                        new StringContent(jsonPayload, Encoding.UTF8, "application/json"), cancellationToken);

                    var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        var isModelNotFound = (int)httpResponse.StatusCode == 404
                            && body.Contains("not found", StringComparison.OrdinalIgnoreCase);

                        if (isModelNotFound && fallbackAttempt == 0)
                        {
                            var fallback = await FindInstalledModelAsync(cancellationToken);
                            if (!string.IsNullOrEmpty(fallback) && !string.Equals(fallback, model, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogWarning("[Ollama] Model '{Model}' not found; falling back to installed model '{Fallback}'", model, fallback);
                                model = fallback;
                                goto NextFallback;
                            }
                        }

                        _logger.LogError("[Ollama] HTTP {StatusCode}: {Body}", httpResponse.StatusCode, body);

                        var retryable = (int)httpResponse.StatusCode >= 500
                            || body.Contains("EOF", StringComparison.OrdinalIgnoreCase)
                            || body.Contains("XML syntax error", StringComparison.OrdinalIgnoreCase);
                        if (retryable && attempt < maxAttempts)
                        {
                            _logger.LogWarning("[Ollama] Retry {Attempt}/{Max} after HTTP {StatusCode}",
                                attempt, maxAttempts, (int)httpResponse.StatusCode);
                            await Task.Delay(1000 * attempt, cancellationToken);
                            continue;
                        }

                        return AIResponse.Failed($"Ollama HTTP {httpResponse.StatusCode}: {body}");
                    }

                    _logger.LogDebug("[Ollama] Response body:\n{Body}", body);

                    var ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(body, JsonOptions);
                    if (ollamaResponse?.Message is null)
                    {
                        if (attempt < maxAttempts)
                        {
                            _logger.LogWarning("[Ollama] Empty response (attempt {Attempt}/{Max}); retrying", attempt, maxAttempts);
                            await Task.Delay(1000 * attempt, cancellationToken);
                            continue;
                        }
                        return AIResponse.Failed("Empty response from Ollama");
                    }

                    var message = ollamaResponse.Message;

                    if (message.ToolCalls is { Count: > 0 })
                    {
                        var calls = new List<AIToolCall>();
                        foreach (var tc in message.ToolCalls)
                        {
                            if (tc.Function is null || string.IsNullOrEmpty(tc.Function.Name)) continue;
                            calls.Add(new AIToolCall(
                                string.IsNullOrEmpty(tc.Id) ? Guid.NewGuid().ToString("N")[..12] : tc.Id,
                                tc.Function.Name,
                                ExtractArguments(tc.Function.Arguments)));
                        }

                        if (calls.Count > 0)
                        {
                            _logger.LogInformation("[Ollama] Tool calls requested: {Calls}",
                                string.Join(", ", calls.Select(c => c.Name)));
                            _monitor.Record(model, ollamaResponse.PromptEvalCount, ollamaResponse.EvalCount, ToMs(ollamaResponse.EvalDuration));
                            return AIResponse.WithToolCalls(calls, model,
                                ollamaResponse.PromptEvalCount, ollamaResponse.EvalCount, ToMs(ollamaResponse.EvalDuration));
                        }
                    }

                    _logger.LogInformation("[Ollama] Text response ({Length} chars): {Preview}",
                        message.Content?.Length ?? 0,
                        message.Content is { Length: > 200 } ? message.Content[..200] + "..." : message.Content);

                    _monitor.Record(model, ollamaResponse.PromptEvalCount, ollamaResponse.EvalCount, ToMs(ollamaResponse.EvalDuration));
                    return AIResponse.Text(message.Content ?? string.Empty, model,
                        ollamaResponse.PromptEvalCount, ollamaResponse.EvalCount, ToMs(ollamaResponse.EvalDuration));
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "[Ollama] Request attempt {Attempt}/{Max} failed", attempt, maxAttempts);
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(1000 * attempt, cancellationToken);
                    }
                }
            }

            _logger.LogError(lastException, "[Ollama] Request failed after {Max} attempts", maxAttempts);
            return AIResponse.Failed($"Ollama error: {lastException?.Message}");

            NextFallback: ;
        }

        return AIResponse.Failed($"Ollama error: no model available");
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestedModel = string.IsNullOrEmpty(request.Model) ? _model : request.Model;
        var model = requestedModel;

        for (var fallbackAttempt = 0; fallbackAttempt < 2; fallbackAttempt++)
        {
            var messages = BuildOllamaMessages(request);
            var tools = ModelCapabilities.SupportsTools(model)
                ? request.Tools
                : Array.Empty<AIToolDefinition>();

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["options"] = new Dictionary<string, object>
                {
                    ["temperature"] = request.Temperature,
                    ["num_predict"] = request.MaxTokens,
                    ["num_ctx"] = _numCtx
                },
                ["stream"] = true,
                ["keep_alive"] = "30m"
            };

            if (tools.Count > 0)
                payload["tools"] = BuildOllamaTools(tools);

            var jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
            _logger.LogInformation("[Ollama] Streaming to {Model}", model);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            string? connectError = null;
            HttpResponseMessage? httpResponse;
            try
            {
                httpResponse = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Ollama] HTTP connection failed for model '{Model}'", model);
                connectError = $"Ollama connection failed: {ex.Message}";
                httpResponse = null;
            }

            if (connectError is not null)
            {
                yield return new AIStreamChunk(Error: connectError);
                yield break;
            }

            if (!httpResponse!.IsSuccessStatusCode)
            {
                var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                var isModelNotFound = (int)httpResponse.StatusCode == 404
                    && body.Contains("not found", StringComparison.OrdinalIgnoreCase);

                if (isModelNotFound && fallbackAttempt == 0)
                {
                    var fallback = await FindInstalledModelAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(fallback) && !string.Equals(fallback, model, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[Ollama] Model '{Model}' not found; falling back to installed model '{Fallback}'", model, fallback);
                        model = fallback;
                        continue;
                    }
                }

                _logger.LogError("[Ollama] HTTP {StatusCode} for model '{Model}': {Body}",
                    (int)httpResponse.StatusCode, model, body);
                yield return new AIStreamChunk(Error: $"Ollama returned {(int)httpResponse.StatusCode} for model '{model}': {body}");
                yield break;
            }

            using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            int promptTokens = 0;
            int completionTokens = 0;
            long evalDurationMs = 0;
            bool sawDoneChunk = false;
            var ttftSw = System.Diagnostics.Stopwatch.StartNew();
            bool firstTokenYielded = false;

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line, JsonOptions);
                if (chunk is null) continue;

                if (chunk.Done)
                {
                    sawDoneChunk = true;
                    promptTokens = chunk.PromptEvalCount;
                    completionTokens = chunk.EvalCount;
                    evalDurationMs = ToMs(chunk.EvalDuration);
                    break;
                }

                if (chunk.Message is null) continue;

                if (chunk.Message.Content is not null)
                {
                    if (!firstTokenYielded)
                    {
                        ttftSw.Stop();
                        _logger.LogInformation("[Ollama] TTFT: {Ttft}ms for model {Model}", ttftSw.ElapsedMilliseconds, model);
                        firstTokenYielded = true;
                    }
                    yield return new AIStreamChunk(Token: chunk.Message.Content);
                }

                if (chunk.Message.ToolCalls is { Count: > 0 })
                {
                    var calls = new List<AIToolCall>();
                    foreach (var tc in chunk.Message.ToolCalls)
                    {
                        if (tc.Function is null || string.IsNullOrEmpty(tc.Function.Name)) continue;
                        calls.Add(new AIToolCall(
                            string.IsNullOrEmpty(tc.Id) ? Guid.NewGuid().ToString("N")[..12] : tc.Id,
                            tc.Function.Name,
                            ExtractArguments(tc.Function.Arguments)));
                    }
                    if (calls.Count > 0)
                    {
                        _logger.LogInformation("[Ollama] Streaming tool calls: {Calls}",
                            string.Join(", ", calls.Select(c => c.Name)));
                        yield return new AIStreamChunk(ToolCalls: calls);
                    }
                }
            }

            if (sawDoneChunk)
            {
                _monitor.Record(model, promptTokens, completionTokens, evalDurationMs);
                var ttftMs = firstTokenYielded ? ttftSw.ElapsedMilliseconds : 0;
                _logger.LogInformation("[Ollama PERF] model={Model} prompt_tokens={Prompt} completion_tokens={Completion} eval_ms={Eval} ttft={Ttft}ms tok/s={Tps:F1}",
                    model, promptTokens, completionTokens, evalDurationMs, ttftMs,
                    evalDurationMs > 0 ? (double)completionTokens / evalDurationMs * 1000 : 0);
                yield return new AIStreamChunk(
                    Done: true,
                    PromptTokens: promptTokens,
                    CompletionTokens: completionTokens,
                    EvalDurationMs: evalDurationMs);
            }

            yield break;
        }
    }

    private async Task<string?> FindInstalledModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out var models)) return null;

            var candidates = new List<(string Name, long Size, bool SupportsTools)>();
            foreach (var m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
                    continue;

                var size = m.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var s) ? s : 0L;
                var supportsTools = false;
                if (m.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                {
                    supportsTools = caps.EnumerateArray().Any(c => c.GetString() == "tools");
                }
                candidates.Add((name.GetString()!, size, supportsTools));
            }

            if (candidates.Count == 0) return null;

            // Préfère le plus petit modèle installé, en donnant la priorité à ceux
            // qui supportent les appels d'outils (utilisés par le pipeline agent).
            return candidates
                .OrderByDescending(c => c.SupportsTools)
                .ThenBy(c => c.Size)
                .First().Name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Ollama] Failed to list installed models for fallback");
        }
        return null;
    }

    private static long ToMs(long nanoseconds)
        => nanoseconds > 0 ? nanoseconds / 1_000_000 : 0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private List<object?> BuildOllamaMessages(AIRequest request)
    {
        var messages = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            }
        };

        foreach (var msg in request.Messages)
        {
            switch (msg.Role)
            {
                case AIMessageRole.System:
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "system",
                        ["content"] = msg.Content
                    });
                    break;

                case AIMessageRole.User:
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = msg.Content
                    });
                    break;

                case AIMessageRole.Assistant:
                    var assistantMsg = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = msg.Content ?? ""
                    };

                    if (msg.ToolCalls is { Count: > 0 })
                    {
                        assistantMsg["tool_calls"] = msg.ToolCalls
                            .Select(tc => new Dictionary<string, object?>
                            {
                                ["id"] = tc.Id,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = tc.Name,
                                    ["arguments"] = (object)tc.Arguments
                                }
                            })
                            .ToList();
                    }

                    messages.Add(assistantMsg);
                    break;

                case AIMessageRole.Tool:
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["content"] = msg.Content,
                        ["tool_call_id"] = msg.ToolCallId
                    });
                    break;

                default:
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = msg.Content
                    });
                    break;
            }
        }

        return messages;
    }

    private static List<object?> BuildOllamaTools(IReadOnlyList<AIToolDefinition> tools)
    {
        var result = new List<object?>(tools.Count);
        foreach (var tool in tools)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["type"] = "object"
            };

            if (tool.Properties.Count > 0)
            {
                var properties = new Dictionary<string, object?>();
                foreach (var prop in tool.Properties)
                {
                    properties[prop.Key] = new Dictionary<string, object?>
                    {
                        ["type"] = prop.Value.Type,
                        ["description"] = prop.Value.Description
                    };
                }
                parameters["properties"] = properties;

                if (tool.Required.Count > 0)
                    parameters["required"] = tool.Required;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters
                }
            });
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ExtractArguments(JsonElement? argumentsElement)
    {
        var result = new Dictionary<string, string>();
        if (argumentsElement is null) return result;

        void Extract(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in element.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
            }
        }

        if (argumentsElement.Value.ValueKind == JsonValueKind.Object)
        {
            Extract(argumentsElement.Value);
        }
        else if (argumentsElement.Value.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsElement.Value.GetString()!);
                Extract(doc.RootElement);
            }
            catch
            {
                result["_raw"] = argumentsElement.Value.GetString() ?? string.Empty;
            }
        }
        else
        {
            result["_raw"] = argumentsElement.Value.GetRawText();
        }

        return result;
    }

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long EvalDuration { get; set; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    private sealed class OllamaToolCall
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("function")]
        public OllamaFunctionCall? Function { get; set; }
    }

    private sealed class OllamaFunctionCall
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public JsonElement? Arguments { get; set; }
    }

    private sealed class OllamaStreamChunk
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long EvalDuration { get; set; }
    }
}
