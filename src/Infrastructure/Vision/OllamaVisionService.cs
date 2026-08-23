using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Vision;

public sealed class OllamaVisionService : IVisionService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaVisionService> _logger;
    private readonly string[] _candidateModels;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private string? _resolvedModel;
    private bool? _available;

    public OllamaVisionService(HttpClient httpClient, ILogger<OllamaVisionService> logger, string? preferredModel = null)
    {
        _http = httpClient;
        _logger = logger;
        _candidateModels = new[] { preferredModel, "llava", "llava:7b" }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct()
            .ToArray();
    }

    public async Task<ImageDescription> DescribeImageAsync(byte[] imageBytes, string? prompt = null, CancellationToken cancellationToken = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return new ImageDescription(string.Empty, false, "No image data provided");

        if (!await IsAvailableAsync(cancellationToken))
            return new ImageDescription(string.Empty, false, $"No vision model available. Tried: {string.Join(", ", _candidateModels)}");

        try
        {
            var userPrompt = string.IsNullOrWhiteSpace(prompt)
                ? "Describe this image in detail. Mention what is visible on screen, including windows, text, buttons and layout."
                : prompt;

            var payload = new
            {
                model = _resolvedModel,
                stream = false,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = userPrompt,
                        images = new[] { Convert.ToBase64String(imageBytes) }
                    }
                }
            };

            using var response = await _http.PostAsJsonAsync("/api/chat", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;

            if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
                return new ImageDescription(string.Empty, false, "Unexpected vision response format");

            var description = content.GetString() ?? string.Empty;
            _logger.LogInformation("[Vision] Image described with model {Model} ({Length} chars)", _resolvedModel, description.Length);
            return new ImageDescription(description, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Vision] Image description failed");
            return new ImageDescription(string.Empty, false, $"Vision error: {ex.Message}");
        }
    }

    private async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
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
                            _logger.LogInformation("[Vision] Model resolved: {Model}", candidate);
                            return true;
                        }
                    }
                }

                _logger.LogWarning("[Vision] No vision model found among: {Models}", string.Join(", ", _candidateModels));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Vision] Ollama unreachable");
            }

            _available = false;
            return false;
        }
        finally
        {
            _initLock.Release();
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
