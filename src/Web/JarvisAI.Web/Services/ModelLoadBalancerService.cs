using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IModelLoadBalancerService
{
    ModelRoute RouteRequest(string taskType, string? preferredModel = null);
    IReadOnlyList<AvailableModel> GetAvailableModels();
    ModelPerformanceStats GetModelStats(string modelId);
    void UpdateRouting(string taskType, string modelId);
    IReadOnlyList<TaskRoutingRule> GetRoutingRules();
}

public sealed class ModelLoadBalancerService : IModelLoadBalancerService
{
    private readonly ILogger<ModelLoadBalancerService> _logger;
    private readonly string _storagePath;
    private readonly List<AvailableModel> _models = new();
    private readonly Dictionary<string, string> _routing = new();
    private readonly List<ModelPerformanceRecord> _performanceRecords = new();

    public ModelLoadBalancerService(ILogger<ModelLoadBalancerService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "model_balancer.json");
        Load();
        InitializeModels();
    }

    public ModelRoute RouteRequest(string taskType, string? preferredModel = null)
    {
        if (preferredModel is not null)
        {
            var preferred = _models.FirstOrDefault(m => m.Id == preferredModel && m.IsAvailable);
            if (preferred is not null)
            {
                return new ModelRoute
                {
                    ModelId = preferred.Id,
                    ModelName = preferred.Name,
                    Reason = "Modèle préféré spécifié"
                };
            }
        }

        // Check routing rules
        if (_routing.TryGetValue(taskType, out var routedModel))
        {
            var model = _models.FirstOrDefault(m => m.Id == routedModel && m.IsAvailable);
            if (model is not null)
            {
                return new ModelRoute
                {
                    ModelId = model.Id,
                    ModelName = model.Name,
                    Reason = $"Règle de routage pour '{taskType}'"
                };
            }
        }

        // Default routing based on task type
        var defaultModel = taskType switch
        {
            "code" => _models.FirstOrDefault(m => m.Id == "codellama"),
            "chat" => _models.FirstOrDefault(m => m.Id == "qwen"),
            "creative" => _models.FirstOrDefault(m => m.Id == "mistral"),
            _ => _models.FirstOrDefault(m => m.IsAvailable)
        };

        if (defaultModel is not null)
        {
            return new ModelRoute
            {
                ModelId = defaultModel.Id,
                ModelName = defaultModel.Name,
                Reason = $"Routage par défaut pour '{taskType}'"
            };
        }

        return new ModelRoute
        {
            ModelId = "qwen3.5:2b",
            ModelName = "Qwen 3.5 2B",
            Reason = "Modèle par défaut"
        };
    }

    public IReadOnlyList<AvailableModel> GetAvailableModels()
        => _models.Where(m => m.IsAvailable).ToList();

    public ModelPerformanceStats GetModelStats(string modelId)
    {
        var records = _performanceRecords.Where(r => r.ModelId == modelId).ToList();

        return new ModelPerformanceStats
        {
            ModelId = modelId,
            TotalRequests = records.Count,
            AverageLatencyMs = records.Any() ? records.Average(r => r.LatencyMs) : 0,
            SuccessRate = records.Any() ? (double)records.Count(r => r.Success) / records.Count * 100 : 0,
            LastUsed = records.Any() ? records.Max(r => r.Timestamp) : DateTime.MinValue
        };
    }

    public void UpdateRouting(string taskType, string modelId)
    {
        _routing[taskType] = modelId;
        Save();
    }

    public IReadOnlyList<TaskRoutingRule> GetRoutingRules()
    {
        return _routing.Select(kv => new TaskRoutingRule
        {
            TaskType = kv.Key,
            ModelId = kv.Value,
            ModelName = _models.FirstOrDefault(m => m.Id == kv.Value)?.Name ?? kv.Value
        }).ToList();
    }

    private void InitializeModels()
    {
        if (_models.Count > 0) return;

        _models.AddRange(new[]
        {
            new AvailableModel { Id = "qwen3.5:2b", Name = "Qwen 3.5 2B", Type = "chat", IsAvailable = true, MaxTokens = 32768 },
            new AvailableModel { Id = "codellama", Name = "CodeLlama", Type = "code", IsAvailable = true, MaxTokens = 16384 },
            new AvailableModel { Id = "mistral", Name = "Mistral", Type = "creative", IsAvailable = true, MaxTokens = 8192 },
            new AvailableModel { Id = "llama2", Name = "Llama 2", Type = "general", IsAvailable = true, MaxTokens = 4096 }
        });

        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("routing", out var routingEl))
                {
                    foreach (var prop in routingEl.EnumerateObject())
                    {
                        _routing[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }

                if (root.TryGetProperty("models", out var modelsEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<AvailableModel>>(modelsEl.GetRawText());
                    if (loaded is not null) _models.AddRange(loaded);
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new { routing = _routing, models = _models };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class AvailableModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsAvailable { get; set; }
    public int MaxTokens { get; set; }
}

public sealed class ModelRoute
{
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class ModelPerformanceStats
{
    public string ModelId { get; set; } = "";
    public int TotalRequests { get; set; }
    public double AverageLatencyMs { get; set; }
    public double SuccessRate { get; set; }
    public DateTime LastUsed { get; set; }
}

public sealed class TaskRoutingRule
{
    public string TaskType { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
}

internal class ModelPerformanceRecord
{
    public string ModelId { get; set; } = "";
    public double LatencyMs { get; set; }
    public bool Success { get; set; }
    public DateTime Timestamp { get; set; }
}
