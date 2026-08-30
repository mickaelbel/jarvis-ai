using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

public interface ITaskDecomposer
{
    TaskDecompositionResult Decompose(string taskDescription);
    IReadOnlyList<TaskStep> GetSteps(string decompositionId);
    void MarkStepComplete(string decompositionId, string stepId);
}

public sealed class TaskDecomposer : ITaskDecomposer
{
    private readonly ILogger<TaskDecomposer> _logger;
    private readonly Dictionary<string, TaskDecompositionResult> _decompositions = new();

    private static readonly Dictionary<string, string[]> TaskPatterns = new()
    {
        ["organize"] = new[] { "Analyser la structure existante", "Identifier les critères de tri", "Déplacer les fichiers", "Vérifier le résultat" },
        ["backup"] = new[] { "Identifier les fichiers à sauvegarder", "Créer le dossier de destination", "Copier les fichiers", "Vérifier l'intégrité" },
        ["install"] = new[] { "Vérifier les prérequis", "Télécharger le package", "Exécuter l'installation", "Configurer post-installation" },
        ["research"] = new[] { "Définir les mots-clés", "Rechercher les sources", "Compiler les résultats", "Synthétiser les conclusions" },
        ["create"] = new[] { "Définir le contenu", "Créer la structure", "Remplir le contenu", "Sauvegarder" },
        ["update"] = new[] { "Lire le contenu actuel", "Identifier les changements", "Appliquer les modifications", "Sauvegarder" },
        ["analyze"] = new[] { "Collecter les données", "Structurer l'analyse", "Identifier les patterns", "Formuler les conclusions" },
        ["automate"] = new[] { "Identifier les étapes manuelles", "Définir les conditions", "Écrire le script", "Tester et valider" },
        ["fix"] = new[] { "Diagnostiquer le problème", "Identifier la cause racine", "Implémenter la correction", "Tester la résolution" },
        ["deploy"] = new[] { "Vérifier l'environnement", "Préparer les assets", "Exécuter le déploiement", "Vérifier le fonctionnement" },
    };

    public TaskDecomposer(ILogger<TaskDecomposer> logger)
    {
        _logger = logger;
    }

    public TaskDecompositionResult Decompose(string taskDescription)
    {
        var lower = taskDescription.ToLowerInvariant();
        var matchedPatterns = new List<string>();

        foreach (var kv in TaskPatterns)
        {
            if (lower.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                matchedPatterns.Add(kv.Key);
        }

        var steps = new List<TaskStep>();
        foreach (var pattern in matchedPatterns)
        {
            if (TaskPatterns.TryGetValue(pattern, out var patternSteps))
            {
                foreach (var stepText in patternSteps)
                {
                    steps.Add(new TaskStep
                    {
                        Id = Guid.NewGuid().ToString("N")[..6],
                        Description = stepText,
                        Status = StepStatus.Pending
                    });
                }
            }
        }

        if (steps.Count == 0)
        {
            steps.Add(new TaskStep { Id = "1", Description = "Analyser la demande", Status = StepStatus.Pending });
            steps.Add(new TaskStep { Id = "2", Description = "Exécuter l'action", Status = StepStatus.Pending });
            steps.Add(new TaskStep { Id = "3", Description = "Vérifier le résultat", Status = StepStatus.Pending });
        }

        var result = new TaskDecompositionResult
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            OriginalTask = taskDescription,
            Steps = steps,
            MatchedPatterns = matchedPatterns,
            CreatedAt = DateTime.UtcNow
        };

        _decompositions[result.Id] = result;
        _logger.LogInformation("[TaskDecompose] '{Task}' → {Steps} steps (patterns: {Patterns})",
            taskDescription[..Math.Min(50, taskDescription.Length)], steps.Count,
            string.Join(", ", matchedPatterns));

        return result;
    }

    public IReadOnlyList<TaskStep> GetSteps(string decompositionId)
    {
        return _decompositions.TryGetValue(decompositionId, out var result)
            ? result.Steps
            : Array.Empty<TaskStep>();
    }

    public void MarkStepComplete(string decompositionId, string stepId)
    {
        if (!_decompositions.TryGetValue(decompositionId, out var result)) return;

        var step = result.Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is not null)
        {
            step.Status = StepStatus.Completed;
            step.CompletedAt = DateTime.UtcNow;

            if (result.Steps.All(s => s.Status == StepStatus.Completed))
                result.Status = DecompositionStatus.Completed;
        }
    }
}

public sealed class TaskDecompositionResult
{
    public string Id { get; set; } = "";
    public string OriginalTask { get; set; } = "";
    public List<TaskStep> Steps { get; set; } = new();
    public List<string> MatchedPatterns { get; set; } = new();
    public DecompositionStatus Status { get; set; } = DecompositionStatus.InProgress;
    public DateTime CreatedAt { get; set; }
}

public sealed class TaskStep
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public StepStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum StepStatus { Pending, InProgress, Completed, Failed }
public enum DecompositionStatus { InProgress, Completed, Failed }
