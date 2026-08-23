using JarvisAI.Application.Memory;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Memory;

public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaEmbeddingService> _logger;
    private readonly string[] _candidateModels;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private string? _resolvedModel;
    private bool? _available;

    public OllamaEmbeddingService(HttpClient httpClient, ILogger<OllamaEmbeddingService> logger, string? preferredModel = null)
    {
        _http = httpClient;
        _logger = logger;
        _candidateModels = new[] { preferredModel, "nomic-embed-text", "qwen3.5:0.8b" }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct()
            .ToArray();
    }

    public string? ModelName => _resolvedModel;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_available.HasValue)
            return _available.Value;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_available.HasValue)
                return _available.Value;

            try
            {
                var tags = await _http.GetFromJsonAsync<OllamaTagsResponse>("/api/tags", cancellationToken);
                if (tags?.Models is not null)
                {
                    foreach (var candidate in _candidateModels)
                    {
                        if (tags.Models.Any(m => string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                        {
                            _resolvedModel = candidate;
                            _available = true;
                            _logger.LogInformation("[Embedding] Model resolved: {Model}", candidate);
                            return true;
                        }
                    }
                }

                _logger.LogWarning("[Embedding] No compatible embedding model found among: {Models}",
                    string.Join(", ", _candidateModels));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Embedding] Ollama unreachable, semantic search disabled");
            }

            _available = false;
            return false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<float[]?> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!await IsAvailableAsync(cancellationToken))
            return null;

        try
        {
            var payload = new { model = _resolvedModel, input = new[] { text } };
            using var response = await _http.PostAsJsonAsync("/api/embed", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (!root.TryGetProperty("embeddings", out var embeddings) || embeddings.GetArrayLength() == 0)
                return null;

            var array = embeddings[0];
            var result = new float[array.GetArrayLength()];
            for (var i = 0; i < result.Length; i++)
                result[i] = array[i].GetSingle();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Embedding] Generation failed");
            return null;
        }
    }

    private sealed class OllamaTagsResponse
    {
        public List<OllamaModelEntry>? Models { get; set; }
    }

    private sealed class OllamaModelEntry
    {
        public string Name { get; set; } = string.Empty;
    }
}
