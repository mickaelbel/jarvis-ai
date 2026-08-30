using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ITutorialService
{
    IReadOnlyList<TutorialStep> GetSteps();
    Task<string> GetCurrentStepAsync(CancellationToken ct = default);
    Task NextStepAsync(CancellationToken ct = default);
    Task PreviousStepAsync(CancellationToken ct = default);
    Task CompleteStepAsync(string stepId, CancellationToken ct = default);
    bool IsCompleted { get; }
    int CurrentStepIndex { get; }
    event EventHandler<TutorialStep>? StepChanged;
}

public sealed class TutorialService : ITutorialService
{
    private readonly ILogger<TutorialService> _logger;
    private readonly string _storagePath;
    private List<TutorialStep> _steps = new();
    private int _currentIndex;
    private HashSet<string> _completedSteps = new();

    public bool IsCompleted => _completedSteps.Count >= _steps.Count;
    public int CurrentStepIndex => _currentIndex;

    public event EventHandler<TutorialStep>? StepChanged;

    public TutorialService(ILogger<TutorialService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "tutorial_progress.json");
        LoadProgress();
        InitializeSteps();
    }

    private void InitializeSteps()
    {
        _steps = new List<TutorialStep>
        {
            new() { Id = "welcome", Title = "Bienvenue !", Description = "Bienvenue dans Jarvis AI. Ce tutoriel va vous guider à travers les fonctionnalités principales.", Order = 0 },
            new() { Id = "chat", Title = "Le Chat", Description = "Utilisez la zone de texte en bas pour poser des questions ou donner des ordres à Jarvis.", Order = 1 },
            new() { Id = "voice", Title = "Commandes Vocales", Description = "Cliquez sur l'icône microphone pour parler à Jarvis. Dites 'Hey Jarvis' pour activer l'écoute.", Order = 2 },
            new() { Id = "tools", Title = "Les Outils", Description = "Jarvis peut exécuter des commandes, manipuler des fichiers, naviguer sur le web et bien plus.", Order = 3 },
            new() { Id = "memory", Title = "Mémoire", Description = "Jarvis se souvient de vos conversations. Dites 'retiens que...' pour sauvegarder une information.", Order = 4 },
            new() { Id = "overlay", Title = "Overlay", Description = "Les réponses s'affichent dans un overlay flottant. Double-cliquez pour l'épingler.", Order = 5 },
            new() { Id = "settings", Title = "Paramètres", Description = "Accédez aux paramètres pour personnaliser la voix, le thème et le comportement de Jarvis.", Order = 6 },
            new() { Id = "complete", Title = "C'est terminé !", Description = "Vous êtes prêt à utiliser Jarvis. N'hésitez pas à explorer et à poser des questions !", Order = 7 },
        };
    }

    public IReadOnlyList<TutorialStep> GetSteps() => _steps.ToList();

    public async Task<string> GetCurrentStepAsync(CancellationToken ct = default)
    {
        if (_currentIndex >= _steps.Count)
            return "Tutoriel terminé !";

        var step = _steps[_currentIndex];
        return await Task.FromResult($"Étape {_currentIndex + 1}/{_steps.Count}: {step.Title}\n\n{step.Description}");
    }

    public async Task NextStepAsync(CancellationToken ct = default)
    {
        if (_currentIndex < _steps.Count - 1)
        {
            _currentIndex++;
            StepChanged?.Invoke(this, _steps[_currentIndex]);
            SaveProgress();
        }
        await Task.CompletedTask;
    }

    public async Task PreviousStepAsync(CancellationToken ct = default)
    {
        if (_currentIndex > 0)
        {
            _currentIndex--;
            StepChanged?.Invoke(this, _steps[_currentIndex]);
            SaveProgress();
        }
        await Task.CompletedTask;
    }

    public async Task CompleteStepAsync(string stepId, CancellationToken ct = default)
    {
        _completedSteps.Add(stepId);
        SaveProgress();
        _logger.LogInformation("[Tutorial] Step completed: {Id}", stepId);

        if (_currentIndex < _steps.Count - 1)
            await NextStepAsync(ct);

        await Task.CompletedTask;
    }

    private void LoadProgress()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                _currentIndex = root.TryGetProperty("currentIndex", out var idx) ? idx.GetInt32() : 0;

                if (root.TryGetProperty("completed", out var completedEl)
                    && completedEl.ValueKind == JsonValueKind.Array)
                {
                    _completedSteps = completedEl.EnumerateArray()
                        .Select(e => e.GetString() ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet();
                }
            }
        }
        catch { }
    }

    private void SaveProgress()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new { currentIndex = _currentIndex, completed = _completedSteps.ToList() };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class TutorialStep
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int Order { get; set; }
}
