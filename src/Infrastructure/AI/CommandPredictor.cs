using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

public interface ICommandPredictor
{
    IReadOnlyList<string> GetPredictions(string currentInput, int maxCount = 5);
    void RecordUsage(string command);
    Task SaveAsync(CancellationToken ct = default);
}

public sealed class CommandPredictor : ICommandPredictor
{
    private readonly ILogger<CommandPredictor> _logger;
    private readonly string _storagePath;
    private readonly Dictionary<string, CommandStats> _stats = new();
    private readonly List<string> _recentCommands = new();
    private const int MaxRecent = 100;

    public CommandPredictor(ILogger<CommandPredictor> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "command_stats.json");
        Load();
    }

    public IReadOnlyList<string> GetPredictions(string currentInput, int maxCount = 5)
    {
        if (string.IsNullOrWhiteSpace(currentInput))
            return GetMostUsed(maxCount);

        var input = currentInput.ToLowerInvariant().Trim();
        var predictions = new List<string>();

        // Exact prefix match
        var prefixMatches = _stats
            .Where(kv => kv.Key.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value.Count)
            .Take(maxCount)
            .Select(kv => kv.Key);
        predictions.AddRange(prefixMatches);

        // Word completion
        if (predictions.Count < maxCount)
        {
            var words = input.Split(' ');
            var lastWord = words.LastOrDefault() ?? "";
            if (!string.IsNullOrEmpty(lastWord))
            {
                var completions = _stats
                    .Where(kv => kv.Key.Contains(lastWord, StringComparison.OrdinalIgnoreCase)
                        && !predictions.Contains(kv.Key))
                    .OrderByDescending(kv => kv.Value.Count)
                    .Take(maxCount - predictions.Count)
                    .Select(kv => kv.Key);
                predictions.AddRange(completions);
            }
        }

        // Time-based suggestions
        var hour = DateTime.Now.Hour;
        var timeMatches = _stats
            .Where(kv => !predictions.Contains(kv.Key)
                && IsTimeRelevant(kv.Value, hour))
            .OrderByDescending(kv => kv.Value.Count)
            .Take(maxCount - predictions.Count)
            .Select(kv => kv.Key);
        predictions.AddRange(timeMatches);

        return predictions.Take(maxCount).ToList();
    }

    public void RecordUsage(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        var key = command.ToLowerInvariant().Trim();
        if (!_stats.ContainsKey(key))
            _stats[key] = new CommandStats();

        _stats[key].Count++;
        _stats[key].LastUsed = DateTime.UtcNow;
        _stats[key].HourCounts[DateTime.Now.Hour] =
            _stats[key].HourCounts.GetValueOrDefault(DateTime.Now.Hour) + 1;

        lock (_recentCommands)
        {
            _recentCommands.Add(key);
            if (_recentCommands.Count > MaxRecent)
                _recentCommands.RemoveAt(0);
        }

        _logger.LogDebug("[Predictor] Recorded: {Command} (total: {Count})",
            command, _stats[key].Count);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new { Stats = _stats, Recent = _recentCommands };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storagePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Predictor] Failed to save");
        }
    }

    private List<string> GetMostUsed(int count)
    {
        return _stats
            .OrderByDescending(kv => kv.Value.Count)
            .Take(count)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static bool IsTimeRelevant(CommandStats stats, int currentHour)
    {
        if (stats.HourCounts.Count == 0) return false;
        var relevantHours = stats.HourCounts
            .Where(kv => Math.Abs(kv.Key - currentHour) <= 2
                || Math.Abs(kv.Key - currentHour) >= 22)
            .Sum(kv => kv.Value);
        return relevantHours > stats.Count * 0.3;
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

                if (root.TryGetProperty("Stats", out var statsEl))
                {
                    foreach (var prop in statsEl.EnumerateObject())
                    {
                        var stats = new CommandStats
                        {
                            Count = prop.Value.GetProperty("Count").GetInt32(),
                            LastUsed = prop.Value.GetProperty("LastUsed").GetDateTime()
                        };
                        _stats[prop.Name] = stats;
                    }
                }
            }
        }
        catch { }
    }
}

internal sealed class CommandStats
{
    public int Count { get; set; }
    public DateTime LastUsed { get; set; }
    public Dictionary<int, int> HourCounts { get; set; } = new();
}
