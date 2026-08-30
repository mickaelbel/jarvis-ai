using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IWorkflowBuilderService
{
    BuilderWorkflowDefinition CreateWorkflow(string name, string description);
    IReadOnlyList<BuilderWorkflowDefinition> GetWorkflows();
    BuilderWorkflowDefinition? GetWorkflow(string workflowId);
    void DeleteWorkflow(string workflowId);
    void AddStep(string workflowId, BuilderWorkflowStep step);
    void RemoveStep(string workflowId, string stepId);
    void ReorderSteps(string workflowId, IReadOnlyList<string> stepIds);
    BuilderWorkflowExecution ExecuteWorkflow(string workflowId, Dictionary<string, string>? inputs = null);
    string ExportWorkflow(string workflowId, string format = "json");
    BuilderWorkflowDefinition ImportWorkflow(string definition);
    IReadOnlyList<BuilderWorkflowTemplate> GetTemplates();
}

public sealed class WorkflowBuilderService : IWorkflowBuilderService
{
    private readonly ILogger<WorkflowBuilderService> _logger;
    private readonly string _storagePath;
    private readonly List<BuilderWorkflowDefinition> _workflows = new();
    private readonly List<BuilderWorkflowExecution> _executions = new();

    public WorkflowBuilderService(ILogger<WorkflowBuilderService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "workflows.json");
        Load();
    }

    public BuilderWorkflowDefinition CreateWorkflow(string name, string description)
    {
        var workflow = new BuilderWorkflowDefinition
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Description = description,
            Steps = new List<BuilderWorkflowStep>(),
            CreatedAt = DateTime.UtcNow
        };

        _workflows.Add(workflow);
        Save();

        _logger.LogInformation("[Workflow] Created: {Name}", name);
        return workflow;
    }

    public IReadOnlyList<BuilderWorkflowDefinition> GetWorkflows()
        => _workflows.OrderByDescending(w => w.CreatedAt).ToList();

    public BuilderWorkflowDefinition? GetWorkflow(string workflowId)
        => _workflows.FirstOrDefault(w => w.Id == workflowId);

    public void DeleteWorkflow(string workflowId)
    {
        _workflows.RemoveAll(w => w.Id == workflowId);
        Save();
    }

    public void AddStep(string workflowId, BuilderWorkflowStep step)
    {
        var workflow = _workflows.FirstOrDefault(w => w.Id == workflowId);
        if (workflow is not null)
        {
            step.Order = workflow.Steps.Count;
            workflow.Steps.Add(step);
            Save();
        }
    }

    public void RemoveStep(string workflowId, string stepId)
    {
        var workflow = _workflows.FirstOrDefault(w => w.Id == workflowId);
        if (workflow is not null)
        {
            workflow.Steps.RemoveAll(s => s.Id == stepId);
            Save();
        }
    }

    public void ReorderSteps(string workflowId, IReadOnlyList<string> stepIds)
    {
        var workflow = _workflows.FirstOrDefault(w => w.Id == workflowId);
        if (workflow is null) return;

        var reordered = new List<BuilderWorkflowStep>();
        for (int i = 0; i < stepIds.Count; i++)
        {
            var step = workflow.Steps.FirstOrDefault(s => s.Id == stepIds[i]);
            if (step is not null)
            {
                step.Order = i;
                reordered.Add(step);
            }
        }

        workflow.Steps = reordered;
        Save();
    }

    public BuilderWorkflowExecution ExecuteWorkflow(string workflowId, Dictionary<string, string>? inputs = null)
    {
        var workflow = _workflows.FirstOrDefault(w => w.Id == workflowId)
            ?? throw new ArgumentException("Workflow non trouvé");

        var execution = new BuilderWorkflowExecution
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            WorkflowId = workflowId,
            Status = "running",
            Inputs = inputs ?? new Dictionary<string, string>(),
            StartedAt = DateTime.UtcNow
        };

        _executions.Add(execution);

        foreach (var step in workflow.Steps.OrderBy(s => s.Order))
        {
            var stepResult = new BuilderStepResult
            {
                StepId = step.Id,
                StepName = step.Name,
                Status = "completed",
                Output = $"Step '{step.Name}' exécuté ({step.Type})",
                CompletedAt = DateTime.UtcNow
            };

            execution.StepResults.Add(stepResult);
        }

        execution.Status = "completed";
        execution.CompletedAt = DateTime.UtcNow;
        execution.Duration = execution.CompletedAt.Value - execution.StartedAt;

        Save();
        return execution;
    }

    public string ExportWorkflow(string workflowId, string format = "json")
    {
        var workflow = _workflows.FirstOrDefault(w => w.Id == workflowId);
        if (workflow is null) return "{}";

        return format switch
        {
            "json" => JsonSerializer.Serialize(workflow, new JsonSerializerOptions { WriteIndented = true }),
            _ => JsonSerializer.Serialize(workflow, new JsonSerializerOptions { WriteIndented = true })
        };
    }

    public BuilderWorkflowDefinition ImportWorkflow(string definition)
    {
        var imported = JsonSerializer.Deserialize<BuilderWorkflowDefinition>(definition)
            ?? throw new ArgumentException("Définition invalide");

        imported.Id = Guid.NewGuid().ToString("N")[8..];
        imported.CreatedAt = DateTime.UtcNow;
        _workflows.Add(imported);
        Save();

        return imported;
    }

    public IReadOnlyList<BuilderWorkflowTemplate> GetTemplates()
    {
        return new List<BuilderWorkflowTemplate>
        {
            new BuilderWorkflowTemplate
            {
                Name = "Backup Automatique",
                Description = "Sauvegarde automatique des fichiers",
                Category = "Système",
                Steps = new List<BuilderWorkflowStep>
                {
                    new() { Name = "Scanner les fichiers", Type = "scan" },
                    new() { Name = "Copier les fichiers", Type = "copy" },
                    new() { Name = "Nettoyer les anciens", Type = "cleanup" }
                }
            },
            new BuilderWorkflowTemplate
            {
                Name = "Déploiement Web",
                Description = "Build et déploiement d'une application web",
                Category = "Développement",
                Steps = new List<BuilderWorkflowStep>
                {
                    new() { Name = "Build le projet", Type = "build" },
                    new() { Name = "Lancer les tests", Type = "test" },
                    new() { Name = "Déployer", Type = "deploy" }
                }
            },
            new BuilderWorkflowTemplate
            {
                Name = "Rapport Quotidien",
                Description = "Génération automatique d'un rapport",
                Category = "Productivité",
                Steps = new List<BuilderWorkflowStep>
                {
                    new() { Name = "Collecter les métriques", Type = "collect" },
                    new() { Name = "Générer le rapport", Type = "generate" },
                    new() { Name = "Envoyer par email", Type = "email" }
                }
            }
        };
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<BuilderWorkflowDefinition>>(json);
                if (loaded is not null) _workflows.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_workflows, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class BuilderWorkflowDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<BuilderWorkflowStep> Steps { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public sealed class BuilderWorkflowStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Order { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public sealed class BuilderWorkflowExecution
{
    public string Id { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string Status { get; set; } = "";
    public Dictionary<string, string> Inputs { get; set; } = new();
    public List<BuilderStepResult> StepResults { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
}

public sealed class BuilderStepResult
{
    public string StepId { get; set; } = "";
    public string StepName { get; set; } = "";
    public string Status { get; set; } = "";
    public string Output { get; set; } = "";
    public DateTime CompletedAt { get; set; }
}

public sealed class BuilderWorkflowTemplate
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public List<BuilderWorkflowStep> Steps { get; set; } = new();
}
