using System.Text.Json;

namespace JarvisAI.Application.AI;

/// <summary>
/// Suit la qualité du routing : regenerations (= miss), thumbs up/down,
/// précision par catégorie. Alimente le dashboard d'analytics (#20).
/// </summary>
public sealed class RoutingFeedbackStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "routing-feedback.json");

    private RoutingFeedbackData _data;
    private readonly object _lock = new();

    public RoutingFeedbackStore()
    {
        _data = Load();
    }

    public void RecordRegeneration(string model, string category)
    {
        lock (_lock)
        {
            var entry = GetOrCreate(model, category);
            entry.Regenerations++;
            entry.TotalRequests++;
            Save();
        }
    }

    public void RecordThumbsUp(string model, string category)
    {
        lock (_lock)
        {
            var entry = GetOrCreate(model, category);
            entry.ThumbsUp++;
            entry.TotalRequests++;
            Save();
        }
    }

    public void RecordThumbsDown(string model, string category)
    {
        lock (_lock)
        {
            var entry = GetOrCreate(model, category);
            entry.ThumbsDown++;
            entry.TotalRequests++;
            Save();
        }
    }

    public RoutingAnalytics GetAnalytics()
    {
        lock (_lock)
        {
            var allEntries = _data.Entries.Values.ToList();
            return new RoutingAnalytics
            {
                TotalRequests = allEntries.Sum(e => e.TotalRequests),
                TotalRegenerations = allEntries.Sum(e => e.Regenerations),
                ThumbsUp = allEntries.Sum(e => e.ThumbsUp),
                ThumbsDown = allEntries.Sum(e => e.ThumbsDown),
                ByCategory = allEntries
                    .GroupBy(e => e.Category)
                    .ToDictionary(g => g.Key, g => new CategoryStats
                    {
                        Requests = g.Sum(e => e.TotalRequests),
                        Regenerations = g.Sum(e => e.Regenerations),
                        Accuracy = g.Sum(e => e.TotalRequests) > 0
                            ? 1.0 - (double)g.Sum(e => e.Regenerations) / g.Sum(e => e.TotalRequests)
                            : 1.0
                    }),
                ByModel = allEntries
                    .GroupBy(e => e.Model)
                    .ToDictionary(g => g.Key, g => new ModelStats
                    {
                        Requests = g.Sum(e => e.TotalRequests),
                        Regenerations = g.Sum(e => e.Regenerations),
                        ThumbsUp = g.Sum(e => g.Sum(e2 => e2.ThumbsUp)),
                        ThumbsDown = g.Sum(e => g.Sum(e2 => e2.ThumbsDown))
                    })
            };
        }
    }

    private RoutingFeedbackEntry GetOrCreate(string model, string category)
    {
        var key = $"{model}|{category}";
        if (!_data.Entries.TryGetValue(key, out var entry))
        {
            entry = new RoutingFeedbackEntry { Model = model, Category = category };
            _data.Entries[key] = entry;
        }
        return entry;
    }

    private RoutingFeedbackData Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                return JsonSerializer.Deserialize<RoutingFeedbackData>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class RoutingFeedbackData
{
    public Dictionary<string, RoutingFeedbackEntry> Entries { get; set; } = new();
}

public sealed class RoutingFeedbackEntry
{
    public string Model { get; set; } = "";
    public string Category { get; set; } = "";
    public int TotalRequests { get; set; }
    public int Regenerations { get; set; }
    public int ThumbsUp { get; set; }
    public int ThumbsDown { get; set; }
}

public sealed class RoutingAnalytics
{
    public int TotalRequests { get; set; }
    public int TotalRegenerations { get; set; }
    public int ThumbsUp { get; set; }
    public int ThumbsDown { get; set; }
    public Dictionary<string, CategoryStats> ByCategory { get; set; } = new();
    public Dictionary<string, ModelStats> ByModel { get; set; } = new();
}

public sealed class CategoryStats
{
    public int Requests { get; set; }
    public int Regenerations { get; set; }
    public double Accuracy { get; set; }
}

public sealed class ModelStats
{
    public int Requests { get; set; }
    public int Regenerations { get; set; }
    public int ThumbsUp { get; set; }
    public int ThumbsDown { get; set; }
}
