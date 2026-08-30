using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ITaskPrioritizationService
{
    IReadOnlyList<PrioritizedTask> GetTasks();
    string AddTask(string title, string description, TaskPriority priority, DateTime? dueDate = null, string? project = null);
    void UpdateTask(string taskId, TaskPriority? priority = null, DateTime? dueDate = null, string? status = null);
    void DeleteTask(string taskId);
    IReadOnlyList<PrioritizedTask> GetPrioritizedTasks();
    TaskPrioritySuggestion SuggestPriority(string title, string description);
    IReadOnlyList<TaskStatistics> GetStatistics();
}

public sealed class TaskPrioritizationService : ITaskPrioritizationService
{
    private readonly ILogger<TaskPrioritizationService> _logger;
    private readonly string _storagePath;
    private readonly List<PrioritizedTask> _tasks = new();

    public TaskPrioritizationService(ILogger<TaskPrioritizationService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "tasks.json");
        Load();
    }

    public IReadOnlyList<PrioritizedTask> GetTasks()
        => _tasks.OrderByDescending(t => t.CalculatedPriority).ThenBy(t => t.DueDate).ToList();

    public string AddTask(string title, string description, TaskPriority priority, DateTime? dueDate = null, string? project = null)
    {
        var task = new PrioritizedTask
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Description = description,
            Priority = priority,
            DueDate = dueDate,
            Project = project ?? "",
            Status = TaskStatus.Todo,
            CreatedAt = DateTime.UtcNow,
            CalculatedPriority = CalculatePriority(priority, dueDate)
        };

        _tasks.Add(task);
        Save();
        return task.Id;
    }

    public void UpdateTask(string taskId, TaskPriority? priority = null, DateTime? dueDate = null, string? status = null)
    {
        var task = _tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null) return;

        if (priority.HasValue) task.Priority = priority.Value;
        if (dueDate.HasValue) task.DueDate = dueDate;
        if (status is not null && Enum.TryParse<TaskStatus>(status, true, out var s))
            task.Status = s;

        task.CalculatedPriority = CalculatePriority(task.Priority, task.DueDate);
        task.LastModified = DateTime.UtcNow;
        Save();
    }

    public void DeleteTask(string taskId)
    {
        _tasks.RemoveAll(t => t.Id == taskId);
        Save();
    }

    public IReadOnlyList<PrioritizedTask> GetPrioritizedTasks()
        => _tasks.Where(t => t.Status != TaskStatus.Done)
                .OrderByDescending(t => t.CalculatedPriority)
                .ThenBy(t => t.DueDate)
                .ToList();

    public TaskPrioritySuggestion SuggestPriority(string title, string description)
    {
        var lower = $"{title} {description}".ToLowerInvariant();
        var suggestedPriority = TaskPriority.Medium;
        var reasons = new List<string>();

        // Urgent keywords
        if (lower.Contains("urgent") || lower.Contains("critique") || lower.Contains("bloquant"))
        {
            suggestedPriority = TaskPriority.Critical;
            reasons.Add("Contient des mots-clés d'urgence");
        }
        else if (lower.Contains("bug") || lower.Contains("erreur") || lower.Contains("crash"))
        {
            suggestedPriority = TaskPriority.High;
            reasons.Add("Related à un bug/erreur");
        }
        else if (lower.Contains("amélioration") || lower.Contains("optimisation"))
        {
            suggestedPriority = TaskPriority.Medium;
            reasons.Add("Amélioration/optimisation");
        }
        else if (lower.Contains("documentation") || lower.Contains("nettoyage"))
        {
            suggestedPriority = TaskPriority.Low;
            reasons.Add("Tâche de maintenance");
        }

        return new TaskPrioritySuggestion
        {
            SuggestedPriority = suggestedPriority,
            Reasons = reasons,
            Confidence = reasons.Count > 0 ? 0.8 : 0.5
        };
    }

    public IReadOnlyList<TaskStatistics> GetStatistics()
    {
        return _tasks.GroupBy(t => t.Status)
                    .Select(g => new TaskStatistics
                    {
                        Status = g.Key.ToString(),
                        Count = g.Count(),
                        AveragePriority = g.Average(t => (int)t.Priority)
                    })
                    .ToList();
    }

    private double CalculatePriority(TaskPriority priority, DateTime? dueDate)
    {
        var baseScore = (double)priority / 4.0 * 100;

        if (dueDate.HasValue)
        {
            var daysUntilDue = (dueDate.Value - DateTime.UtcNow).TotalDays;
            if (daysUntilDue < 0)
                baseScore += 50; // Overdue
            else if (daysUntilDue < 1)
                baseScore += 30; // Due today
            else if (daysUntilDue < 3)
                baseScore += 20; // Due soon
            else if (daysUntilDue < 7)
                baseScore += 10; // Due this week
        }

        return baseScore;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<PrioritizedTask>>(json);
                if (loaded is not null) _tasks.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_tasks, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class PrioritizedTask
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public TaskPriority Priority { get; set; }
    public DateTime? DueDate { get; set; }
    public string Project { get; set; } = "";
    public TaskStatus Status { get; set; }
    public double CalculatedPriority { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModified { get; set; }
}

public sealed class TaskPrioritySuggestion
{
    public TaskPriority SuggestedPriority { get; set; }
    public List<string> Reasons { get; set; } = new();
    public double Confidence { get; set; }
}

public sealed class TaskStatistics
{
    public string Status { get; set; } = "";
    public int Count { get; set; }
    public double AveragePriority { get; set; }
}

public enum TaskPriority { Low = 0, Medium = 1, High = 2, Critical = 3 }
public enum TaskStatus { Todo, InProgress, Done, Blocked }
