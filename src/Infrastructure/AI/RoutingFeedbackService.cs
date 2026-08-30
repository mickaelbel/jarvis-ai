using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

public interface IRoutingFeedbackService
{
    void RecordRouting(string message, string selectedModel, double confidence);
    void RecordCorrection(string message, string correctModel);
    string? GetSuggestedModel(string message);
    Task SaveAsync(CancellationToken ct = default);
}

public sealed class RoutingFeedbackService : IRoutingFeedbackService
{
    private readonly ILogger<RoutingFeedbackService> _logger;
    private readonly string _storagePath;
    private readonly Dictionary<string, ModelRoutingStats> _stats = new();
    private readonly List<RoutingRecord> _recent = new();
    private const int MaxRecent = 500;

    public RoutingFeedbackService(ILogger<RoutingFeedbackService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "routing_feedback.json");
        Load();
    }

    public void RecordRouting(string message, string selectedModel, double confidence)
    {
        var key = ExtractKey(message);
        if (key is null) return;

        if (!_stats.TryGetValue(key, out var stats))
        {
            stats = new ModelRoutingStats();
            _stats[key] = stats;
        }

        stats.Count++;
        stats.LastUsed = DateTime.UtcNow;

        if (!stats.ModelCounts.ContainsKey(selectedModel))
            stats.ModelCounts[selectedModel] = 0;
        stats.ModelCounts[selectedModel]++;

        _recent.Add(new RoutingRecord
        {
            Message = message,
            Model = selectedModel,
            Confidence = confidence,
            Timestamp = DateTime.UtcNow
        });

        if (_recent.Count > MaxRecent)
            _recent.RemoveAt(0);
    }

    public void RecordCorrection(string message, string correctModel)
    {
        var key = ExtractKey(message);
        if (key is null) return;

        if (!_stats.TryGetValue(key, out var stats))
        {
            stats = new ModelRoutingStats();
            _stats[key] = stats;
        }

        stats.Corrections++;
        if (!stats.ModelCounts.ContainsKey(correctModel))
            stats.ModelCounts[correctModel] = 0;
        stats.ModelCounts[correctModel]++;

        _logger.LogInformation("[RoutingFeedback] Correction recorded for key '{Key}': {Model}",
            key, correctModel);
    }

    public string? GetSuggestedModel(string message)
    {
        var key = ExtractKey(message);
        if (key is null || !_stats.TryGetValue(key, out var stats)) return null;

        if (stats.Count < 3) return null;

        var best = stats.ModelCounts
            .OrderByDescending(kv => kv.Value)
            .FirstOrDefault();

        if (best.Value < 2) return null;

        _logger.LogDebug("[RoutingFeedback] Suggested model for '{Key}': {Model} ({Count} uses)",
            key, best.Key, best.Value);

        return best.Key;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new RoutingFeedbackData
            {
                Stats = _stats,
                Recent = _recent.TakeLast(100).ToList()
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storagePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RoutingFeedback] Failed to save");
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<RoutingFeedbackData>(json);
                if (data is not null)
                {
                    foreach (var kv in data.Stats)
                        _stats[kv.Key] = kv.Value;
                    _recent.AddRange(data.Recent);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string? ExtractKey(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var words = message.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Take(3));
    }
}

internal sealed class ModelRoutingStats
{
    public int Count { get; set; }
    public int Corrections { get; set; }
    public DateTime LastUsed { get; set; }
    public Dictionary<string, int> ModelCounts { get; set; } = new();
}

internal sealed class RoutingRecord
{
    public string Message { get; set; } = "";
    public string Model { get; set; } = "";
    public double Confidence { get; set; }
    public DateTime Timestamp { get; set; }
}

internal sealed class RoutingFeedbackData
{
    public Dictionary<string, ModelRoutingStats> Stats { get; set; } = new();
    public List<RoutingRecord> Recent { get; set; } = new();
}
