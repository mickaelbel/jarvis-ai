using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IDeploymentPipelineService
{
    IReadOnlyList<Pipeline> GetPipelines();
    Pipeline? GetPipeline(string pipelineId);
    string CreatePipeline(string name, string description, List<PipelineStage> stages);
    PipelineExecutionResult ExecutePipeline(string pipelineId, string? branch = null);
    IReadOnlyList<PipelineExecution> GetExecutions(string pipelineId);
    void DeletePipeline(string pipelineId);
}

public sealed class DeploymentPipelineService : IDeploymentPipelineService
{
    private readonly ILogger<DeploymentPipelineService> _logger;
    private readonly string _storagePath;
    private readonly List<Pipeline> _pipelines = new();
    private readonly List<PipelineExecution> _executions = new();

    public DeploymentPipelineService(ILogger<DeploymentPipelineService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "pipelines.json");
        Load();
        InitializeDefaults();
    }

    public IReadOnlyList<Pipeline> GetPipelines()
        => _pipelines;

    public Pipeline? GetPipeline(string pipelineId)
        => _pipelines.FirstOrDefault(p => p.Id == pipelineId);

    public string CreatePipeline(string name, string description, List<PipelineStage> stages)
    {
        var pipeline = new Pipeline
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Description = description,
            Stages = stages,
            CreatedAt = DateTime.UtcNow
        };

        _pipelines.Add(pipeline);
        Save();
        return pipeline.Id;
    }

    public PipelineExecutionResult ExecutePipeline(string pipelineId, string? branch = null)
    {
        var pipeline = GetPipeline(pipelineId);
        if (pipeline is null)
            return new PipelineExecutionResult { Success = false, Message = "Pipeline non trouvé" };

        var execution = new PipelineExecution
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            PipelineId = pipelineId,
            PipelineName = pipeline.Name,
            Branch = branch ?? "main",
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
            StageResults = new List<StageResult>()
        };

        // Simulate stage execution
        foreach (var stage in pipeline.Stages)
        {
            execution.StageResults.Add(new StageResult
            {
                StageName = stage.Name,
                Status = ExecutionStatus.Completed,
                Duration = TimeSpan.FromSeconds(new Random().Next(5, 30)),
                Output = $"Stage '{stage.Name}' exécuté avec succès"
            });
        }

        execution.Status = ExecutionStatus.Completed;
        execution.CompletedAt = DateTime.UtcNow;
        execution.TotalDuration = execution.CompletedAt.Value - execution.StartedAt;

        _executions.Add(execution);
        Save();

        _logger.LogInformation("[Pipeline] {Name} executed in {Duration}s", pipeline.Name, execution.TotalDuration.TotalSeconds);

        return new PipelineExecutionResult
        {
            Success = true,
            ExecutionId = execution.Id,
            Message = $"Pipeline '{pipeline.Name}' exécuté avec succès",
            Duration = execution.TotalDuration
        };
    }

    public IReadOnlyList<PipelineExecution> GetExecutions(string pipelineId)
        => _executions.Where(e => e.PipelineId == pipelineId)
                      .OrderByDescending(e => e.StartedAt)
                      .ToList();

    public void DeletePipeline(string pipelineId)
    {
        _pipelines.RemoveAll(p => p.Id == pipelineId);
        Save();
    }

    private void InitializeDefaults()
    {
        if (_pipelines.Count > 0) return;

        _pipelines.Add(new Pipeline
        {
            Id = "default-deploy",
            Name = "Déploiement Standard",
            Description = "Build → Test → Deploy",
            Stages = new()
            {
                new() { Name = "Build", Command = "dotnet build -c Release", Order = 1 },
                new() { Name = "Test", Command = "dotnet test", Order = 2 },
                new() { Name = "Publish", Command = "dotnet publish -c Release", Order = 3 },
                new() { Name = "Deploy", Command = "copy to production", Order = 4 }
            },
            CreatedAt = DateTime.UtcNow
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

                if (root.TryGetProperty("pipelines", out var pipelinesEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<Pipeline>>(pipelinesEl.GetRawText());
                    if (loaded is not null) _pipelines.AddRange(loaded);
                }

                if (root.TryGetProperty("executions", out var execEl))
                {
                    var loaded = JsonSerializer.Deserialize<List<PipelineExecution>>(execEl.GetRawText());
                    if (loaded is not null) _executions.AddRange(loaded);
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

            var data = new { pipelines = _pipelines, executions = _executions };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class Pipeline
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PipelineStage> Stages { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public sealed class PipelineStage
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public int Order { get; set; }
}

public sealed class PipelineExecution
{
    public string Id { get; set; } = "";
    public string PipelineId { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public string Branch { get; set; } = "main";
    public ExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public List<StageResult> StageResults { get; set; } = new();
}

public sealed class StageResult
{
    public string StageName { get; set; } = "";
    public ExecutionStatus Status { get; set; }
    public TimeSpan Duration { get; set; }
    public string Output { get; set; } = "";
}

public sealed class PipelineExecutionResult
{
    public bool Success { get; set; }
    public string? ExecutionId { get; set; }
    public string Message { get; set; } = "";
    public TimeSpan Duration { get; set; }
}
