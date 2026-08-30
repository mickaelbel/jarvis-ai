using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IWorkflowTemplateService
{
    IReadOnlyList<WorkflowTemplate> GetTemplates(string? category = null);
    WorkflowTemplate? GetTemplate(string templateId);
    string CreateTemplate(string name, string description, List<WorkflowStep> steps, string category);
    void DeleteTemplate(string templateId);
    WorkflowExecutionResult ExecuteTemplate(string templateId, Dictionary<string, string>? variables = null);
    IReadOnlyList<WorkflowExecution> GetExecutions(string templateId);
}

public sealed class WorkflowTemplateService : IWorkflowTemplateService
{
    private readonly ILogger<WorkflowTemplateService> _logger;
    private readonly string _storagePath;
    private readonly List<WorkflowTemplate> _templates = new();
    private readonly List<WorkflowExecution> _executions = new();

    public WorkflowTemplateService(ILogger<WorkflowTemplateService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "workflow_templates.json");
        Load();
        InitializeDefaults();
    }

    public IReadOnlyList<WorkflowTemplate> GetTemplates(string? category = null)
    {
        if (category is null) return _templates;
        return _templates.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public WorkflowTemplate? GetTemplate(string templateId)
        => _templates.FirstOrDefault(t => t.Id == templateId);

    public string CreateTemplate(string name, string description, List<WorkflowStep> steps, string category)
    {
        var template = new WorkflowTemplate
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Description = description,
            Steps = steps,
            Category = category,
            CreatedAt = DateTime.UtcNow
        };

        _templates.Add(template);
        Save();
        return template.Id;
    }

    public void DeleteTemplate(string templateId)
    {
        _templates.RemoveAll(t => t.Id == templateId);
        Save();
    }

    public WorkflowExecutionResult ExecuteTemplate(string templateId, Dictionary<string, string>? variables = null)
    {
        var template = GetTemplate(templateId);
        if (template is null)
            return new WorkflowExecutionResult { Success = false, Message = "Template non trouvé" };

        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            TemplateId = templateId,
            TemplateName = template.Name,
            StartedAt = DateTime.UtcNow,
            Status = ExecutionStatus.Running
        };

        _executions.Add(execution);
        Save();

        _logger.LogInformation("[Workflow] Executing: {Name} ({Steps} steps)", template.Name, template.Steps.Count);

        // Simulate execution
        execution.CompletedAt = DateTime.UtcNow;
        execution.Status = ExecutionStatus.Completed;
        execution.Log.Add($"Workflow '{template.Name}' exécuté avec succès");
        Save();

        return new WorkflowExecutionResult
        {
            Success = true,
            ExecutionId = execution.Id,
            Message = $"Workflow '{template.Name}' exécuté",
            StepsExecuted = template.Steps.Count
        };
    }

    public IReadOnlyList<WorkflowExecution> GetExecutions(string templateId)
        => _executions.Where(e => e.TemplateId == templateId).ToList();

    private void InitializeDefaults()
    {
        if (_templates.Count > 0) return;

        var defaults = new List<WorkflowTemplate>
        {
            new()
            {
                Name = "Setup Projet",
                Description = "Initialiser un nouveau projet avec Git, README et structure",
                Category = "Development",
                Steps = new()
                {
                    new() { Action = "scaffold", Parameters = "template=console" },
                    new() { Action = "git_init", Parameters = "" },
                    new() { Action = "create_readme", Parameters = "" }
                }
            },
            new()
            {
                Name = "Déploiement",
                Description = "Build, test et déployer l'application",
                Category = "DevOps",
                Steps = new()
                {
                    new() { Action = "dotnet_build", Parameters = "configuration=Release" },
                    new() { Action = "dotnet_test", Parameters = "" },
                    new() { Action = "dotnet_publish", Parameters = "output=dist" },
                    new() { Action = "deploy", Parameters = "target=production" }
                }
            },
            new()
            {
                Name = "Backup Complet",
                Description = "Sauvegarder données, configs et mémoires",
                Category = "Maintenance",
                Steps = new()
                {
                    new() { Action = "backup_configs", Parameters = "" },
                    new() { Action = "backup_memories", Parameters = "" },
                    new() { Action = "backup_templates", Parameters = "" },
                    new() { Action = "compress", Parameters = "format=zip" }
                }
            }
        };

        foreach (var template in defaults)
        {
            template.Id = Guid.NewGuid().ToString("N")[..8];
            template.CreatedAt = DateTime.UtcNow;
            foreach (var step in template.Steps)
                step.Id = Guid.NewGuid().ToString("N")[..6];
            _templates.Add(template);
        }

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

                if (root.TryGetProperty("templates", out var templatesEl))
                {
                    var templatesJson = templatesEl.GetRawText();
                    var loaded = JsonSerializer.Deserialize<List<WorkflowTemplate>>(templatesJson);
                    if (loaded is not null) _templates.AddRange(loaded);
                }

                if (root.TryGetProperty("executions", out var execEl))
                {
                    var execJson = execEl.GetRawText();
                    var loaded = JsonSerializer.Deserialize<List<WorkflowExecution>>(execJson);
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

            var data = new { templates = _templates, executions = _executions };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class WorkflowTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public List<WorkflowStep> Steps { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public sealed class WorkflowStep
{
    public string Id { get; set; } = "";
    public string Action { get; set; } = "";
    public string Parameters { get; set; } = "";
}

public sealed class WorkflowExecution
{
    public string Id { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public ExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<string> Log { get; set; } = new();
}

public sealed class WorkflowExecutionResult
{
    public bool Success { get; set; }
    public string? ExecutionId { get; set; }
    public string Message { get; set; } = "";
    public int StepsExecuted { get; set; }
}

public enum ExecutionStatus { Pending, Running, Completed, Failed }
