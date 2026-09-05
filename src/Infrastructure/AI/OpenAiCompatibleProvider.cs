using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisAI.Infrastructure.AI;

/// <summary>
/// Fournisseur OpenAI-compatible générique : OpenAI, Groq, Google Gemini
/// (endpoint OpenAI-compatible), OpenRouter, Hugging Face et serveurs locaux
/// (LM Studio / llama.cpp) parlent tous le protocole /v1/chat/completions.
/// La configuration (clé API, endpoint, modèles) est lue dynamiquement depuis
/// AiProviderSettingsStore : modifier une clé dans les paramètres est appliqué
/// immédiatement, sans redémarrage.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAIProvider
{
    private readonly string _providerKey;
    private readonly AiProviderCatalogEntry _catalog;
    private readonly AiProviderSettingsStore _store;
    private readonly ILogger<OpenAiCompatibleProvider> _logger;

    public string Name
    {
        get
        {
            var settings = GetSettings();
            return string.IsNullOrWhiteSpace(settings?.DisplayName)
                ? _catalog.DisplayName
                : settings!.DisplayName;
        }
    }

    public OpenAiCompatibleProvider(
        string providerKey,
        string displayName,
        AiProviderSettingsStore store,
        AiProviderCatalogEntry catalog,
        ILogger<OpenAiCompatibleProvider> logger)
    {
        _providerKey = providerKey;
        _store = store;
        _catalog = catalog;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            var settings = GetSettings();
            if (settings is null || !settings.Enabled) return false;
            if (string.IsNullOrWhiteSpace(settings.BaseUrl)) return false;
            if (_catalog.RequiresKey && string.IsNullOrWhiteSpace(settings.ApiKey)) return false;
            return true;
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(IsAvailable);

    public IReadOnlyList<string> KnownModels
    {
        get
        {
            var settings = GetSettings();
            var models = new List<string>(_catalog.Models);
            if (settings is not null)
                foreach (var m in settings.Models)
                    if (!models.Contains(m, StringComparer.OrdinalIgnoreCase))
                        models.Add(m);
            return models;
        }
    }

    public bool MatchesModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var m = model.Trim();
        foreach (var km in KnownModels)
        {
            if (string.Equals(km, m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var p in _catalog.ModelPrefixes)
        {
            if (m.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public async Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        if (!IsAvailable)
            return AIResponse.Failed($"{Name} n'est pas configuré (clé API ou endpoint manquant).");

        var client = CreateClient(settings);
        var model = string.IsNullOrEmpty(request.Model) ? _catalog.DefaultModel : request.Model;
        if (string.IsNullOrWhiteSpace(model)) model = settings.DefaultModel;
        var messages = BuildOpenAIMessages(request);
        var tools = BuildOpenAITools(request.Tools);

        var payload = new
        {
            model,
            messages,
            tools = tools.Count > 0 ? tools : null,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens
        };

        _logger.LogDebug("[{Provider}] Sending request to {Model} with {ToolCount} tools", Name, model, tools.Count);

        try
        {
            var httpResponse = await client.PostAsJsonAsync(_catalog.ChatPath, payload, cancellationToken);
            var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger.LogError("[{Provider}] HTTP {StatusCode}: {Body}", Name, httpResponse.StatusCode, body);
                return AIResponse.Failed($"{Name} HTTP {httpResponse.StatusCode}: {body}");
            }

            var completion = JsonSerializer.Deserialize<OpenAICompletion>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var choice = completion?.Choices?.FirstOrDefault();
            if (choice?.Message is null)
                return AIResponse.Failed($"Réponse vide de {Name}");

            var message = choice.Message;
            var toolCalls = new List<AIToolCall>();

            if (message.ToolCalls?.Count > 0)
            {
                foreach (var tc in message.ToolCalls)
                {
                    var args = string.IsNullOrEmpty(tc.Function?.Arguments)
                        ? new Dictionary<string, string>()
                        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tc.Function!.Arguments!)
                            ?.ToDictionary(k => k.Key, k => k.Value.ValueKind == JsonValueKind.String ? k.Value.GetString()! : k.Value.ToString())
                        ?? new Dictionary<string, string>();

                    toolCalls.Add(new AIToolCall(
                        id: tc.Id ?? Guid.NewGuid().ToString("N")[..12],
                        name: tc.Function?.Name ?? "unknown",
                        args));
                }

                _logger.LogInformation("[{Provider}] Response contains {ToolCallCount} tool calls", Name, toolCalls.Count);
                return AIResponse.WithToolCalls(toolCalls, model, completion?.Usage?.PromptTokens ?? 0, completion?.Usage?.CompletionTokens ?? 0);
            }

            _logger.LogInformation("[{Provider}] Text response ({Length} chars)", Name, message.Content?.Length ?? 0);
            return AIResponse.Text(message.Content ?? string.Empty, model, completion?.Usage?.PromptTokens ?? 0, completion?.Usage?.CompletionTokens ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Provider}] Request failed", Name);
            return AIResponse.Failed($"{Name} error: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        if (!IsAvailable)
        {
            yield return new AIStreamChunk(Error: $"{Name} n'est pas configuré (clé API ou endpoint manquant).");
            yield break;
        }

        var client = CreateClient(settings);
        var model = string.IsNullOrEmpty(request.Model) ? _catalog.DefaultModel : request.Model;
        if (string.IsNullOrWhiteSpace(model)) model = settings.DefaultModel;
        var messages = BuildOpenAIMessages(request);
        var tools = BuildOpenAITools(request.Tools);

        var payload = new
        {
            model,
            messages,
            tools = tools.Count > 0 ? tools : null,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            stream = true
        };

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, _catalog.ChatPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        string? connectError = null;
        HttpResponseMessage? httpResponse;
        try
        {
            httpResponse = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Provider}] HTTP connection failed for model '{Model}'", Name, model);
            connectError = $"{Name} connection failed: {ex.Message}";
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
            _logger.LogError("[{Provider}] HTTP {StatusCode} for model '{Model}': {Body}",
                Name, (int)httpResponse.StatusCode, model, body);
            yield return new AIStreamChunk(Error: $"{Name} returned {(int)httpResponse.StatusCode}: {body}");
            yield break;
        }

        var toolCallBuffers = new List<OpenAIToolCallBuffer>();

        using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;

            var chunk = JsonSerializer.Deserialize<OpenAIStreamChunk>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var delta = chunk?.Choices?.FirstOrDefault()?.Delta;
            if (delta is null) continue;

            if (delta.Content is not null)
                yield return new AIStreamChunk(Token: delta.Content);

            if (delta.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in delta.ToolCalls)
                {
                    var buffer = toolCallBuffers.FirstOrDefault(b => b.Index == tc.Index);
                    if (buffer is null)
                    {
                        buffer = new OpenAIToolCallBuffer { Index = tc.Index };
                        toolCallBuffers.Add(buffer);
                    }
                    if (!string.IsNullOrEmpty(tc.Id)) buffer.Id = tc.Id;
                    if (!string.IsNullOrEmpty(tc.Function?.Name)) buffer.Name = tc.Function.Name;
                    if (!string.IsNullOrEmpty(tc.Function?.Arguments)) buffer.Arguments.Append(tc.Function.Arguments);
                }
            }
        }

        if (toolCallBuffers.Count > 0)
        {
            var calls = new List<AIToolCall>();
            foreach (var buffer in toolCallBuffers)
            {
                if (string.IsNullOrEmpty(buffer.Name)) continue;
                var args = new Dictionary<string, string>();
                try
                {
                    using var doc = JsonDocument.Parse(buffer.Arguments.ToString());
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString()!
                            : prop.Value.ToString();
                    }
                }
                catch { /* ignore malformed arguments */ }

                calls.Add(new AIToolCall(
                    string.IsNullOrEmpty(buffer.Id) ? Guid.NewGuid().ToString("N")[..12] : buffer.Id,
                    buffer.Name,
                    args));
            }
            yield return new AIStreamChunk(ToolCalls: calls);
        }
    }

    private AiProviderSettings? GetSettings()
    {
        if (_store.Get().TryGetValue(_providerKey, out var settings)) return settings;
        return null;
    }

    private static readonly ConcurrentDictionary<string, HttpClient> _clientCache = new(StringComparer.OrdinalIgnoreCase);

    private HttpClient CreateClient(AiProviderSettings settings)
    {
        var cacheKey = $"{_providerKey}|{settings.BaseUrl}|{settings.ApiKey?[..Math.Min(8, settings.ApiKey?.Length ?? 0)]}";
        return _clientCache.GetOrAdd(cacheKey, _ =>
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            var client = new HttpClient(handler) { BaseAddress = new Uri(settings.BaseUrl), Timeout = TimeSpan.FromMinutes(2) };
            if (_catalog.RequiresKey && !string.IsNullOrWhiteSpace(settings.ApiKey))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
            if (_providerKey == "openrouter")
                client.DefaultRequestHeaders.Add("HTTP-Referer", "https://jarvis-ai.local");
            return client;
        });
    }

    private sealed class OpenAIToolCallBuffer
    {
        public int Index { get; set; }
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }

    private static List<object> BuildOpenAIMessages(AIRequest request)
    {
        var messages = new List<object>();
        messages.Add(new { role = "system", content = request.SystemPrompt });

        foreach (var msg in request.Messages)
        {
            if (msg.Role == AIMessageRole.System) continue;
            messages.Add(new { role = msg.Role.ToString().ToLowerInvariant(), content = msg.Content });
        }

        return messages;
    }

    private static List<object> BuildOpenAITools(IReadOnlyList<AIToolDefinition> tools)
    {
        return tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = new
                {
                    type = "object",
                    properties = t.Properties.ToDictionary(
                        p => p.Key,
                        p => new { type = p.Value.Type, description = p.Value.Description }),
                    required = t.Required
                }
            }
        }).Cast<object>().ToList();
    }

    private sealed class OpenAICompletion
    {
        [JsonPropertyName("choices")]
        public List<OpenAIChoice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public OpenAIUsage? Usage { get; set; }
    }

    private sealed class OpenAIChoice
    {
        [JsonPropertyName("message")]
        public OpenAIMessage? Message { get; set; }
    }

    private sealed class OpenAIMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OpenAIToolCall>? ToolCalls { get; set; }
    }

    private sealed class OpenAIToolCall
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("function")]
        public OpenAIFunction? Function { get; set; }
    }

    private sealed class OpenAIFunction
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }

    private sealed class OpenAIUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }
    }

    private sealed class OpenAIStreamChunk
    {
        [JsonPropertyName("choices")]
        public List<OpenAIStreamChoice>? Choices { get; set; }
    }

    private sealed class OpenAIStreamChoice
    {
        [JsonPropertyName("delta")]
        public OpenAIStreamDelta? Delta { get; set; }
    }

    private sealed class OpenAIStreamDelta
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OpenAIStreamToolCall>? ToolCalls { get; set; }
    }

    private sealed class OpenAIStreamToolCall
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("function")]
        public OpenAIStreamToolCallFunction? Function { get; set; }
    }

    private sealed class OpenAIStreamToolCallFunction
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }
}
