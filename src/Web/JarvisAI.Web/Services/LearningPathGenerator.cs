using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ILearningPathGenerator
{
    LearningPath GeneratePath(string topic, string currentLevel, LearningGoal goal);
    IReadOnlyList<LearningPath> GetSavedPaths();
    LearningPath? GetPath(string pathId);
    void SavePath(LearningPath path);
    void DeletePath(string pathId);
    IReadOnlyList<string> GetAvailableTopics();
    IReadOnlyList<string> GetAvailableLevels();
}

public sealed class LearningPathGenerator : ILearningPathGenerator
{
    private readonly ILogger<LearningPathGenerator> _logger;
    private readonly string _storagePath;
    private readonly List<LearningPath> _savedPaths = new();

    private static readonly Dictionary<string, List<string>> TopicPrerequisites = new()
    {
        ["C#"] = new() { "Programmation", "POO" },
        ["ASP.NET"] = new() { "C#", "Web" },
        ["Blazor"] = new() { "ASP.NET", "HTML/CSS" },
        ["Docker"] = new() { "Linux", "Réseau" },
        ["Kubernetes"] = new() { "Docker", "YAML" },
        ["Git"] = new() { "Ligne de commande" },
        ["SQL"] = new() { "Bases de données" },
        ["Machine Learning"] = new() { "Python", "Mathématiques" },
        ["AI/LLM"] = new() { "Python", "API REST" }
    };

    private static readonly Dictionary<string, List<LearningResource>> TopicResources = new()
    {
        ["C#"] = new()
        {
            new() { Title = "C# Fundamentals", Type = ResourceType.Video, Url = "https://learn.microsoft.com/dotnet/csharp/" },
            new() { Title = "Microsoft Learn C#", Type = ResourceType.Article, Url = "https://learn.microsoft.com/dotnet/csharp/" },
            new() { Title = "Exercices C#", Type = ResourceType.Exercise, Url = "https://www.codingame.com/" }
        },
        ["Docker"] = new()
        {
            new() { Title = "Docker Getting Started", Type = ResourceType.Video, Url = "https://docs.docker.com/get-started/" },
            new() { Title = "Docker Tutorial", Type = ResourceType.Article, Url = "https://docs.docker.com/" }
        },
        ["Git"] = new()
        {
            new() { Title = "Git Tutorial", Type = ResourceType.Video, Url = "https://git-scm.com/doc" },
            new() { Title = "Learn Git Branching", Type = ResourceType.Interactive, Url = "https://learngitbranching.js.org/" }
        }
    };

    public LearningPathGenerator(ILogger<LearningPathGenerator> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "learning_paths.json");
        Load();
    }

    public LearningPath GeneratePath(string topic, string currentLevel, LearningGoal goal)
    {
        var steps = new List<LearningStep>();
        var prerequisites = TopicPrerequisites.GetValueOrDefault(topic) ?? new();

        // Add prerequisite steps
        foreach (var prereq in prerequisites)
        {
            steps.Add(new LearningStep
            {
                Id = Guid.NewGuid().ToString("N")[..6],
                Title = $"Apprendre {prereq}",
                Description = $"Maîtriser les bases de {prereq}",
                EstimatedHours = 10,
                Resources = new(),
                Status = StepProgress.Pending
            });
        }

        // Add main topic steps
        var levelSteps = currentLevel switch
        {
            "débutant" => new[] { "Bases", "Syntaxe", "Projets simples" },
            "intermédiaire" => new[] { "Concepts avancés", "Design Patterns", "Projets moyens" },
            "avancé" => new[] { "Architecture", "Performance", "Projets complexes" },
            _ => new[] { "Bases", "Pratique" }
        };

        foreach (var step in levelSteps)
        {
            steps.Add(new LearningStep
            {
                Id = Guid.NewGuid().ToString("N")[..6],
                Title = $"{topic} - {step}",
                Description = $"Étape {step} pour {topic}",
                EstimatedHours = goal switch
                {
                    LearningGoal.Understand => 5,
                    LearningGoal.Build => 15,
                    LearningGoal.Master => 30,
                    _ => 10
                },
                Resources = TopicResources.GetValueOrDefault(topic) ?? new(),
                Status = StepProgress.Pending
            });
        }

        var path = new LearningPath
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Topic = topic,
            CurrentLevel = currentLevel,
            Goal = goal,
            Steps = steps,
            TotalEstimatedHours = steps.Sum(s => s.EstimatedHours),
            CreatedAt = DateTime.UtcNow
        };

        _logger.LogInformation("[Learning] Generated path: {Topic} ({Level}) - {Steps} steps",
            topic, currentLevel, steps.Count);

        return path;
    }

    public IReadOnlyList<LearningPath> GetSavedPaths()
        => _savedPaths.OrderByDescending(p => p.CreatedAt).ToList();

    public LearningPath? GetPath(string pathId)
        => _savedPaths.FirstOrDefault(p => p.Id == pathId);

    public void SavePath(LearningPath path)
    {
        var existing = _savedPaths.FirstOrDefault(p => p.Id == path.Id);
        if (existing is not null)
            _savedPaths.Remove(existing);

        _savedPaths.Add(path);
        Save();
    }

    public void DeletePath(string pathId)
    {
        _savedPaths.RemoveAll(p => p.Id == pathId);
        Save();
    }

    public IReadOnlyList<string> GetAvailableTopics()
        => TopicPrerequisites.Keys.OrderBy(t => t).ToList();

    public IReadOnlyList<string> GetAvailableLevels()
        => new[] { "débutant", "intermédiaire", "avancé", "expert" };

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<LearningPath>>(json);
                if (loaded is not null) _savedPaths.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_savedPaths, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class LearningPath
{
    public string Id { get; set; } = "";
    public string Topic { get; set; } = "";
    public string CurrentLevel { get; set; } = "";
    public LearningGoal Goal { get; set; }
    public List<LearningStep> Steps { get; set; } = new();
    public int TotalEstimatedHours { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class LearningStep
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int EstimatedHours { get; set; }
    public List<LearningResource> Resources { get; set; } = new();
    public StepProgress Status { get; set; }
}

public sealed class LearningResource
{
    public string Title { get; set; } = "";
    public ResourceType Type { get; set; }
    public string Url { get; set; } = "";
}

public enum LearningGoal { Understand, Build, Master }
public enum StepProgress { Pending, InProgress, Completed }
public enum ResourceType { Video, Article, Exercise, Interactive, Book }
