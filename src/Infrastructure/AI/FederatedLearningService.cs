using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

public interface IFederatedLearningService
{
    Task RecordInteractionAsync(string input, string expectedTool, double confidence, CancellationToken ct = default);
    string? GetSuggestedTool(string input);
    Task<LearningStats> GetStatsAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

public sealed class FederatedLearningService : IFederatedLearningService
{
    private readonly ILogger<FederatedLearningService> _logger;
    private readonly string _storagePath;
    private readonly Dictionary<string, ToolPrediction> _predictions = new();
    private readonly List<InteractionRecord> _recentInteractions = new();
    private const int MaxRecent = 1000;

    public FederatedLearningService(ILogger<FederatedLearningService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "federated_learning.json");
        Load();
    }

    public async Task RecordInteractionAsync(string input, string expectedTool, double confidence, CancellationToken ct = default)
    {
        var key = ExtractKey(input);
        if (key is null) return;

        if (!_predictions.ContainsKey(key))
            _predictions[key] = new ToolPrediction();

        var pred = _predictions[key];
        if (!pred.ToolCounts.ContainsKey(expectedTool))
            pred.ToolCounts[expectedTool] = 0;
        pred.ToolCounts[expectedTool]++;
        pred.TotalCount++;
        pred.LastSeen = DateTime.UtcNow;

        _recentInteractions.Add(new InteractionRecord
        {
            Input = input,
            Key = key,
            Tool = expectedTool,
            Confidence = confidence,
            Timestamp = DateTime.UtcNow
        });

        if (_recentInteractions.Count > MaxRecent)
            _recentInteractions.RemoveAt(0);

        _logger.LogDebug("[Federated] Recorded: {Key} → {Tool} (count: {Count})",
            key, expectedTool, pred.ToolCounts[expectedTool]);

        await Task.CompletedTask;
    }

    public string? GetSuggestedTool(string input)
    {
        var key = ExtractKey(input);
        if (key is null || !_predictions.TryGetValue(key, out var pred)) return null;

        if (pred.TotalCount < 3) return null;

        var best = pred.ToolCounts
            .OrderByDescending(kv => kv.Value)
            .FirstOrDefault();

        var confidence = (double)best.Value / pred.TotalCount;
        if (confidence < 0.6) return null;

        _logger.LogDebug("[Federated] Suggestion for '{Key}': {Tool} (confidence: {Conf:P0})",
            key, best.Key, confidence);

        return best.Key;
    }

    public async Task<LearningStats> GetStatsAsync(CancellationToken ct = default)
    {
        return await Task.FromResult(new LearningStats
        {
            TotalPatterns = _predictions.Count,
            TotalInteractions = _recentInteractions.Count,
            TopTools = _predictions.Values
                .SelectMany(p => p.ToolCounts)
                .GroupBy(kv => kv.Key)
                .OrderByDescending(g => g.Sum(kv => kv.Value))
                .Take(5)
                .Select(g => new ToolStat { Tool = g.Key, Count = g.Sum(kv => kv.Value) })
                .ToList()
        });
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new { Predictions = _predictions, Recent = _recentInteractions.TakeLast(100).ToList() };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storagePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Federated] Failed to save");
        }
    }

    private static string? ExtractKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var words = input.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Take(4));
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

                if (root.TryGetProperty("Predictions", out var predEl))
                {
                    foreach (var prop in predEl.EnumerateObject())
                    {
                        var pred = new ToolPrediction
                        {
                            TotalCount = prop.Value.GetProperty("TotalCount").GetInt32(),
                            LastSeen = prop.Value.GetProperty("LastSeen").GetDateTime()
                        };
                        if (prop.Value.TryGetProperty("ToolCounts", out var tcEl))
                        {
                            foreach (var tc in tcEl.EnumerateObject())
                                pred.ToolCounts[tc.Name] = tc.Value.GetInt32();
                        }
                        _predictions[prop.Name] = pred;
                    }
                }
            }
        }
        catch { }
    }
}

internal sealed class ToolPrediction
{
    public Dictionary<string, int> ToolCounts { get; set; } = new();
    public int TotalCount { get; set; }
    public DateTime LastSeen { get; set; }
}

internal sealed class InteractionRecord
{
    public string Input { get; set; } = "";
    public string Key { get; set; } = "";
    public string Tool { get; set; } = "";
    public double Confidence { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class LearningStats
{
    public int TotalPatterns { get; set; }
    public int TotalInteractions { get; set; }
    public List<ToolStat> TopTools { get; set; } = new();
}

public sealed class ToolStat
{
    public string Tool { get; set; } = "";
    public int Count { get; set; }
}
