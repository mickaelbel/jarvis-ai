using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IAbTestingService
{
    string CreateExperiment(string name, string description, List<string> variants);
    void AssignVariant(string experimentId, string userId);
    void TrackEvent(string experimentId, string userId, string eventName, Dictionary<string, string>? properties = null);
    ExperimentResult GetResult(string experimentId);
    IReadOnlyList<Experiment> GetExperiments();
    void EndExperiment(string experimentId);
}

public sealed class AbTestingService : IAbTestingService
{
    private readonly ILogger<AbTestingService> _logger;
    private readonly string _storagePath;
    private readonly List<Experiment> _experiments = new();
    private readonly Dictionary<string, Dictionary<string, string>> _assignments = new();
    private readonly List<ExperimentEvent> _events = new();

    public AbTestingService(ILogger<AbTestingService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "ab_testing.json");
        Load();
    }

    public string CreateExperiment(string name, string description, List<string> variants)
    {
        var experiment = new Experiment
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Description = description,
            Variants = variants,
            Status = ExperimentStatus.Running,
            CreatedAt = DateTime.UtcNow
        };

        _experiments.Add(experiment);
        Save();
        return experiment.Id;
    }

    public void AssignVariant(string experimentId, string userId)
    {
        var experiment = _experiments.FirstOrDefault(e => e.Id == experimentId);
        if (experiment is null) return;

        if (!_assignments.ContainsKey(experimentId))
            _assignments[experimentId] = new Dictionary<string, string>();

        // Simple hash-based assignment
        var hash = Math.Abs(userId.GetHashCode()) % experiment.Variants.Count;
        _assignments[experimentId][userId] = experiment.Variants[hash];
        Save();
    }

    public void TrackEvent(string experimentId, string userId, string eventName, Dictionary<string, string>? properties = null)
    {
        var variant = GetVariant(experimentId, userId);

        _events.Add(new ExperimentEvent
        {
            ExperimentId = experimentId,
            UserId = userId,
            Variant = variant,
            EventName = eventName,
            Properties = properties ?? new(),
            Timestamp = DateTime.UtcNow
        });

        Save();
    }

    public ExperimentResult GetResult(string experimentId)
    {
        var experiment = _experiments.FirstOrDefault(e => e.Id == experimentId);
        if (experiment is null)
            return new ExperimentResult { Error = "Expérience non trouvée" };

        var events = _events.Where(e => e.ExperimentId == experimentId).ToList();
        var variantResults = new Dictionary<string, VariantResult>();

        foreach (var variant in experiment.Variants)
        {
            var variantEvents = events.Where(e => e.Variant == variant).ToList();
            var conversions = variantEvents.Count(e => e.EventName == "conversion");

            variantResults[variant] = new VariantResult
            {
                Name = variant,
                Participants = variantEvents.Select(e => e.UserId).Distinct().Count(),
                Conversions = conversions,
                ConversionRate = variantEvents.Select(e => e.UserId).Distinct().Count() > 0
                    ? (double)conversions / variantEvents.Select(e => e.UserId).Distinct().Count() * 100
                    : 0
            };
        }

        return new ExperimentResult
        {
            ExperimentId = experimentId,
            ExperimentName = experiment.Name,
            Status = experiment.Status,
            VariantResults = variantResults,
            TotalParticipants = events.Select(e => e.UserId).Distinct().Count(),
            GeneratedAt = DateTime.UtcNow
        };
    }

    public IReadOnlyList<Experiment> GetExperiments()
        => _experiments.OrderByDescending(e => e.CreatedAt).ToList();

    public void EndExperiment(string experimentId)
    {
        var experiment = _experiments.FirstOrDefault(e => e.Id == experimentId);
        if (experiment is not null)
        {
            experiment.Status = ExperimentStatus.Completed;
            experiment.EndedAt = DateTime.UtcNow;
            Save();
        }
    }

    private string GetVariant(string experimentId, string userId)
    {
        if (_assignments.TryGetValue(experimentId, out var assignments) &&
            assignments.TryGetValue(userId, out var variant))
        {
            return variant;
        }
        return "control";
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

                if (root.TryGetProperty("experiments", out var expsEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<Experiment>>(expsEl.GetRawText());
                    if (loaded is not null) _experiments.AddRange(loaded);
                }

                if (root.TryGetProperty("events", out var eventsEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<ExperimentEvent>>(eventsEl.GetRawText());
                    if (loaded is not null) _events.AddRange(loaded);
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

            var data = new { experiments = _experiments, events = _events };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class Experiment
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Variants { get; set; } = new();
    public ExperimentStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

public sealed class ExperimentEvent
{
    public string ExperimentId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Variant { get; set; } = "";
    public string EventName { get; set; } = "";
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

public sealed class ExperimentResult
{
    public string? ExperimentId { get; set; }
    public string ExperimentName { get; set; } = "";
    public ExperimentStatus Status { get; set; }
    public Dictionary<string, VariantResult> VariantResults { get; set; } = new();
    public int TotalParticipants { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class VariantResult
{
    public string Name { get; set; } = "";
    public int Participants { get; set; }
    public int Conversions { get; set; }
    public double ConversionRate { get; set; }
}

public enum ExperimentStatus { Draft, Running, Completed, Paused }
