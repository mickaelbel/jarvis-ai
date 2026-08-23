using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public sealed class OllamaModelService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaModelService> _logger;

    public OllamaModelService(HttpClient httpClient, ILogger<OllamaModelService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _httpClient.GetAsync("/api/tags", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _httpClient.GetAsync("/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return new();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var models = new List<OllamaLocalModel>();

            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    models.Add(new OllamaLocalModel
                    {
                        Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Size = m.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                        Digest = m.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "",
                        ModifiedAt = m.TryGetProperty("modified_at", out var ma) ? ma.GetString() ?? "" : ""
                    });
                }
            }

            return models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OllamaModel] Failed to get local models");
            return new();
        }
    }

    public async Task<List<OllamaPullProgress>> PullModelAsync(string modelName, IProgress<OllamaPullProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<OllamaPullProgress>();
        try
        {
            var payload = new { name = modelName, stream = true };
            var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/pull") { Content = content };
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                if (string.IsNullOrWhiteSpace(line)) continue;

                var chunk = JsonSerializer.Deserialize<OllamaPullChunk>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (chunk != null)
                {
                    var progressInfo = new OllamaPullProgress
                    {
                        Status = chunk.Status ?? "",
                        Completed = chunk.Completed,
                        Total = chunk.Total
                    };
                    results.Add(progressInfo);
                    progress?.Report(progressInfo);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OllamaModel] Failed to pull model {Model}", modelName);
        }

        return results;
    }

    public async Task<bool> DeleteModelAsync(string modelName, CancellationToken ct = default)
    {
        try
        {
            var payload = new { name = modelName };
            var response = await _httpClient.PostAsJsonAsync("/api/delete", payload, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OllamaModel] Failed to delete model {Model}", modelName);
            return false;
        }
    }

    public async Task<bool> TestChatEndpointAsync(CancellationToken ct = default)
    {
        try
        {
            var payload = new { model = "test", messages = Array.Empty<object>(), stream = false };
            var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync("/api/chat", content, ct);
            _logger.LogInformation("[OllamaModel] /api/chat test returned {StatusCode}", resp.StatusCode);
            return resp.StatusCode != System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OllamaModel] /api/chat test failed");
            return false;
        }
    }

    public async Task<OllamaDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var diag = new OllamaDiagnostics();
        try
        {
            var tagsResp = await _httpClient.GetAsync("/api/tags", ct);
            diag.TagsEndpoint = (int)tagsResp.StatusCode;
            diag.TagsOk = tagsResp.IsSuccessStatusCode;
        }
        catch (Exception ex) { diag.TagsError = ex.Message; }
        try
        {
            var payload = new { model = "test", messages = Array.Empty<object>(), stream = false };
            var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            var chatResp = await _httpClient.PostAsync("/api/chat", content, ct);
            diag.ChatEndpoint = (int)chatResp.StatusCode;
        }
        catch (Exception ex) { diag.ChatError = ex.Message; }
        return diag;
    }

    public async Task<List<OllamaProcessInfo>> GetRunningModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _httpClient.GetAsync("/api/ps", ct);
            if (!resp.IsSuccessStatusCode) return new();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var processes = new List<OllamaProcessInfo>();

            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var size = m.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    var sizeVram = m.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0;
                    string? processor = null;
                    string? parameterSize = null;
                    string? quantization = null;
                    if (m.TryGetProperty("details", out var details))
                    {
                        processor = details.TryGetProperty("processor", out var p) ? p.GetString() : null;
                        parameterSize = details.TryGetProperty("parameter_size", out var ps) ? ps.GetString() : null;
                        quantization = details.TryGetProperty("quantization_level", out var q) ? q.GetString() : null;
                    }

                    processes.Add(new OllamaProcessInfo
                    {
                        Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Size = size,
                        SizeVram = sizeVram,
                        ExpiresAt = m.TryGetProperty("expires_at", out var e) ? e.GetString() ?? "" : "",
                        Processor = processor ?? "",
                        ParameterSize = parameterSize ?? "",
                        Quantization = quantization ?? ""
                    });
                }
            }

            return processes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OllamaModel] Failed to get running models");
            return new();
        }
    }

    public async Task<bool> PreloadAsync(string modelName, string keepAlive = "30m", CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = modelName,
                ["prompt"] = "",
                ["stream"] = false,
                ["keep_alive"] = keepAlive
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync("/api/generate", content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OllamaModel] Preload of {Model} failed", modelName);
            return false;
        }
    }

    public static List<OllamaCatalogModel> GetCatalog()
    {
        return new()
        {
            // Généralistes / chat
            new("qwen3.5", "Modèle généraliste Alibaba, très bon en FR et en raisonnement", "2B/8B/14B/32B", "Alibaba", new() { "general", "chat", "reasoning", "multilingual" }, "2B / 8B / 14B / 32B", "1.6 Go / 5.2 Go / 9 Go / 19 Go"),
            new("qwen3", "Modèle généraliste Alibaba (pensée et non-pensée)", "8B/32B", "Alibaba", new() { "general", "chat", "reasoning" }, "8B / 32B", "4.7 Go / 19 Go"),
            new("llama3.1", "Dernier modèle Llama de Meta", "8B", "Meta", new() { "general", "chat", "reasoning" }, "8B", "4.7 Go"),
            new("llama3.2", "Llama léger pour les appareils de bord", "1B/3B", "Meta", new() { "general", "fast", "edge" }, "1B / 3B", "1.3 Go / 2.0 Go"),
            new("llama3.3", "Llama avancé (raisonnement)", "70B", "Meta", new() { "reasoning", "coding", "advanced" }, "70B", "40 Go"),
            new("gemma3", "Modèle Google, excellent multilingue et petite VRAM", "4B/12B/27B", "Google", new() { "general", "multilingual", "reasoning" }, "4B / 12B / 27B", "3.3 Go / 8.1 Go / 16 Go"),
            new("gemma2", "Gemma 2 de Google", "2B/9B/27B", "Google", new() { "general", "multilingual" }, "2B / 9B / 27B", "1.6 Go / 5.5 Go / 16 Go"),
            new("phi4", "Microsoft, raisonnement pour sa taille", "14B", "Microsoft", new() { "general", "reasoning" }, "14B", "9.1 Go"),
            new("phi3", "Petit modèle Microsoft", "3.8B", "Microsoft", new() { "general", "fast", "small" }, "3.8B", "2.3 Go"),
            new("mistral", "Modèle Mistral AI", "7B", "Mistral AI", new() { "general", "fast" }, "7B", "4.4 Go"),
            new("mistral-small", "Mistral Small, bon compromis", "24B", "Mistral AI", new() { "general", "reasoning" }, "24B", "16 Go"),
            new("command-r", "Cohere Command R (RAG)", "35B", "Cohere", new() { "general", "RAG", "enterprise" }, "35B", "21 Go"),
            new("gpt-oss", "Modèle open-source le plus récent (anciennement gpt-oss)", "20B", "OpenAI", new() { "general", "reasoning", "coding" }, "20B", "10 Go"),
            new("granite3.1-dense", "IBM, dense et efficace", "8B", "IBM", new() { "general", "chat" }, "8B", "5 Go"),
            new("dolphin", "Modèle sans censure", "7B/13B", "Eric Hartford", new() { "uncensored", "general" }, "7B / 13B", "4.4 Go / 8 Go"),
            new("neural-chat", "Modèle de chat Intel", "7B", "Intel", new() { "chat", "general" }, "7B", "4.4 Go"),
            new("starling-lm", "Starling de Berkeley", "7B", "Berkeley", new() { "chat", "reasoning" }, "7B", "4.4 Go"),
            new("openhermes", "Fine-tune Hermes", "7B", "NousResearch", new() { "chat", "general" }, "7B", "4.4 Go"),
            new("solar", "Upstage Solar", "10.7B", "Upstage", new() { "general", "reasoning" }, "10.7B", "6.6 Go"),
            new("vicuna", "Fine-tune Llama", "7B/13B/33B", "LMSYS", new() { "chat", "general" }, "7B / 13B / 33B", "4.4 Go / 8 Go / 20 Go"),
            new("yi", "Modèle 01.AI", "6B/9B/34B", "01.AI", new() { "general", "multilingual" }, "6B / 9B / 34B", "3.6 Go / 5.6 Go / 20 Go"),
            new("tinyllama", "Tiny Llama pour les petits systèmes", "1.1B", "TinyLlama", new() { "tiny", "edge", "fast" }, "1.1B", "0.6 Go"),

            // Codage
            new("qwen3-coder", "Modèle de codage Alibaba très performant", "8B/14B/30B/235B", "Alibaba", new() { "coding", "programming", "agentic" }, "8B / 14B / 30B", "5.4 Go / 9 Go / 19 Go"),
            new("qwen2.5-coder", "Modèle de codage Qwen", "7B/14B/32B", "Alibaba", new() { "coding", "programming" }, "7B / 14B / 32B", "4.7 Go / 9 Go / 19 Go"),
            new("codellama", "Llama spécialisé codage", "7B/13B/34B", "Meta", new() { "coding", "programming" }, "7B / 13B / 34B", "4.4 Go / 8 Go / 20 Go"),
            new("deepseek-coder", "Modèle de codage DeepSeek", "6.7B/33B", "DeepSeek", new() { "coding", "programming" }, "6.7B / 33B", "3.8 Go / 20 Go"),
            new("codestral", "Modèle de codage Mistral", "22B", "Mistral AI", new() { "coding", "programming" }, "22B", "13 Go"),
            new("starcoder2", "Modèle de codage BigCode", "3B/7B/15B", "BigCode", new() { "coding", "programming" }, "3B / 7B / 15B", "1.8 Go / 4.4 Go / 9 Go"),

            // Raisonnement / mathématiques
            new("deepseek-r1", "Modèle de raisonnement DeepSeek", "1.5B/7B/8B/14B/32B/70B", "DeepSeek", new() { "reasoning", "math", "advanced" }, "1.5B / 7B / 8B / 14B / 32B / 70B", "1.1 Go / 4.7 Go / 4.9 Go / 9 Go / 20 Go / 43 Go"),
            new("deepseek-r1-0528", "DeepSeek R1 affiné (répétitions réduites)", "1.5B/8B/14B/32B/70B", "DeepSeek", new() { "reasoning", "math", "advanced" }, "1.5B / 8B / 14B / 32B / 70B", "1.1 Go / 4.9 Go / 9 Go / 20 Go / 43 Go"),
            new("qwen3-reasoning", "Qwen3 en mode pensée", "8B/32B", "Alibaba", new() { "reasoning", "math" }, "8B / 32B", "4.7 Go / 19 Go"),
            new("qwq", "Qwen à double raisonnement", "32B", "Qwen Team", new() { "reasoning", "math", "advanced" }, "32B", "20 Go"),
            new("olmo2", "AllenAI OLMo 2, solide en raisonnement", "7B/13B", "AllenAI", new() { "reasoning", "general" }, "7B / 13B", "4.5 Go / 8.5 Go"),
            new("mixtral", "Mixture of experts Mistral", "8x7B", "Mistral AI", new() { "reasoning", "advanced" }, "8x7B", "26 Go"),

            // Multilingue / divers
            new("qwen2.5", "Qwen multilingue Alibaba", "0.5B-72B", "Alibaba", new() { "general", "coding", "multilingual" }, "0.5B / 1.5B / 3B / 7B / 14B / 32B / 72B", "0.4 Go / 1 Go / 1.9 Go / 4.7 Go / 9 Go / 19 Go / 44 Go"),
            new("aya-expanse", "Modèle multilingue Cohere", "8B/32B", "Cohere", new() { "multilingual", "general" }, "8B / 32B", "5 Go / 19 Go"),
            new("nemotron-mini", "NVIDIA, petit et rapide", "4B", "NVIDIA", new() { "general", "fast" }, "4B", "2.7 Go"),
        };
    }

    public static OllamaCatalogModel? FindCatalogModel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var baseName = name.Trim().Split(':')[0].ToLowerInvariant();
        return GetCatalog().FirstOrDefault(m => string.Equals(m.Name, baseName, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class OllamaLocalModel
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Digest { get; set; } = "";
    public string ModifiedAt { get; set; } = "";
    public string SizeDisplay => FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public sealed class OllamaProcessInfo
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long SizeVram { get; set; }
    public long SizeRam => Size - SizeVram;
    public string ExpiresAt { get; set; } = "";
    public string Processor { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string Quantization { get; set; } = "";

    public string SizeDisplay => FormatSize(Size);
    public string VramDisplay => FormatSize(SizeVram);
    public string RamDisplay => FormatSize(SizeRam);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public sealed class OllamaCatalogModel
{
    public string Name { get; }
    public string Description { get; }
    public string Sizes { get; }
    public string Author { get; }
    public List<string> Tags { get; }
    public string Parameters { get; }
    public string DownloadSize { get; }
    public IReadOnlyList<OllamaCatalogVariant> Variants { get; }

    public OllamaCatalogModel(string name, string description, string sizes, string author, List<string> tags,
        string parameters = "", string downloadSize = "")
    {
        Name = name;
        Description = description;
        Sizes = sizes;
        Author = author;
        Tags = tags;
        Parameters = parameters;
        DownloadSize = downloadSize;
        Variants = BuildVariants(name, parameters, downloadSize);
    }

    private static IReadOnlyList<OllamaCatalogVariant> BuildVariants(string name, string parameters, string downloadSize)
    {
        var baseName = name.Trim().Split(':')[0].ToLowerInvariant();

        var paramsList = string.IsNullOrWhiteSpace(parameters)
            ? Array.Empty<string>()
            : parameters.Split('/').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        var sizesList = string.IsNullOrWhiteSpace(downloadSize)
            ? Array.Empty<string>()
            : downloadSize.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        if (paramsList.Length == 0)
            return new[] { new OllamaCatalogVariant(baseName, "", "") };

        var variants = new List<OllamaCatalogVariant>(paramsList.Length);
        for (var i = 0; i < paramsList.Length; i++)
        {
            var size = sizesList.Length > i ? sizesList[i] : "";
            var tag = $"{baseName}:{paramsList[i].Trim().ToLowerInvariant().Replace(" ", "")}";
            variants.Add(new OllamaCatalogVariant(tag, paramsList[i], size));
        }
        return variants;
    }
}

public sealed record OllamaCatalogVariant(string Tag, string SizeLabel, string DownloadLabel);

public sealed class OllamaPullProgress
{
    public string Status { get; set; } = "";
    public long Completed { get; set; }
    public long Total { get; set; }
    public double Percent => Total > 0 ? (double)Completed / Total * 100 : 0;
}

internal sealed class OllamaPullChunk
{
    public string? Status { get; set; }
    public long Completed { get; set; }
    public long Total { get; set; }
}

public sealed class OllamaDiagnostics
{
    public int TagsEndpoint { get; set; }
    public bool TagsOk { get; set; }
    public string? TagsError { get; set; }
    public int ChatEndpoint { get; set; }
    public string? ChatError { get; set; }
    public bool ChatAvailable => ChatEndpoint != 404 && ChatError == null;
}
