using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IModelFineTuningService
{
    FineTuningJob CreateJob(string modelName, IReadOnlyList<TrainingExample> examples, FineTuningConfig config);
    IReadOnlyList<FineTuningJob> GetJobs();
    FineTuningJob? GetJob(string jobId);
    void CancelJob(string jobId);
    TrainingProgress GetProgress(string jobId);
    IReadOnlyList<TrainingExample> GetTrainingExamples(string jobId);
}

public sealed class ModelFineTuningService : IModelFineTuningService
{
    private readonly ILogger<ModelFineTuningService> _logger;
    private readonly string _storagePath;
    private readonly List<FineTuningJob> _jobs = new();
    private readonly Dictionary<string, List<TrainingExample>> _examples = new();
    private readonly Dictionary<string, TrainingProgress> _progress = new();

    public ModelFineTuningService(ILogger<ModelFineTuningService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "fine_tuning.json");
        Load();
    }

    public FineTuningJob CreateJob(string modelName, IReadOnlyList<TrainingExample> examples, FineTuningConfig config)
    {
        var job = new FineTuningJob
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            ModelName = modelName,
            Config = config,
            Status = "pending",
            ExamplesCount = examples.Count,
            CreatedAt = DateTime.UtcNow
        };

        _jobs.Add(job);
        _examples[job.Id] = examples.ToList();
        _progress[job.Id] = new TrainingProgress
        {
            JobId = job.Id,
            Status = "pending",
            EpochsCompleted = 0,
            TotalEpochs = config.Epochs,
            Loss = 0,
            Accuracy = 0,
            StartedAt = DateTime.UtcNow
        };

        Save();
        _logger.LogInformation("[FineTuning] Created job: {Id} for {Model} with {Count} examples",
            job.Id, modelName, examples.Count);

        // Simulate training start
        SimulateTraining(job.Id);

        return job;
    }

    public IReadOnlyList<FineTuningJob> GetJobs()
        => _jobs.OrderByDescending(j => j.CreatedAt).ToList();

    public FineTuningJob? GetJob(string jobId)
        => _jobs.FirstOrDefault(j => j.Id == jobId);

    public void CancelJob(string jobId)
    {
        var job = _jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is not null)
        {
            job.Status = "cancelled";
            if (_progress.TryGetValue(jobId, out var progress))
                progress.Status = "cancelled";
            Save();
        }
    }

    public TrainingProgress GetProgress(string jobId)
        => _progress.TryGetValue(jobId, out var p) ? p : new TrainingProgress { JobId = jobId, Status = "unknown" };

    public IReadOnlyList<TrainingExample> GetTrainingExamples(string jobId)
        => _examples.TryGetValue(jobId, out var exs) ? exs : new List<TrainingExample>();

    private void SimulateTraining(string jobId)
    {
        var progress = _progress[jobId];
        progress.Status = "training";
        progress.StartedAt = DateTime.UtcNow;

        // Simulate progress updates
        Task.Run(async () =>
        {
            for (int epoch = 0; epoch < progress.TotalEpochs; epoch++)
            {
                await Task.Delay(500); // Simulate work
                progress.EpochsCompleted = epoch + 1;
                progress.Loss = Math.Max(0.1, 1.0 - (epoch * 0.15));
                progress.Accuracy = Math.Min(0.95, 0.5 + (epoch * 0.1));
            }

            progress.Status = "completed";
            progress.CompletedAt = DateTime.UtcNow;

            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
                job.Status = "completed";

            Save();
            _logger.LogInformation("[FineTuning] Job {Id} completed", jobId);
        });
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

                if (root.TryGetProperty("jobs", out var jobsEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<FineTuningJob>>(jobsEl.GetRawText());
                    if (loaded is not null) _jobs.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(new { jobs = _jobs }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class FineTuningJob
{
    public string Id { get; set; } = "";
    public string ModelName { get; set; } = "";
    public FineTuningConfig Config { get; set; } = new();
    public string Status { get; set; } = "";
    public int ExamplesCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class FineTuningConfig
{
    public int Epochs { get; set; } = 3;
    public double LearningRate { get; set; } = 0.001;
    public int BatchSize { get; set; } = 8;
    public double ValidationSplit { get; set; } = 0.2;
}

public sealed class TrainingExample
{
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public string? Category { get; set; }
    public double Weight { get; set; } = 1.0;
}

public sealed class TrainingProgress
{
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "";
    public int EpochsCompleted { get; set; }
    public int TotalEpochs { get; set; }
    public double Loss { get; set; }
    public double Accuracy { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
