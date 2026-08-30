using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Security;

public sealed class ErrorLearningService : IErrorLearningService
{
    private readonly ILogger<ErrorLearningService> _logger;
    private readonly ConcurrentDictionary<string, ErrorRecord> _errors = new();
    private readonly string _persistPath;

    public ErrorLearningService(ILogger<ErrorLearningService> logger)
    {
        _logger = logger;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI");
        Directory.CreateDirectory(dir);
        _persistPath = Path.Combine(dir, "error_learning.json");
        Load();
    }

    public Task RecordErrorAsync(string toolName, string action, string error, string? context = null, CancellationToken ct = default)
    {
        var key = $"{toolName}:{action}:{NormalizeError(error)}";

        _errors.AddOrUpdate(key,
            _ => new ErrorRecord(
                Guid.NewGuid(), DateTime.Now, toolName, action,
                error, context, GetSuggestionForError(error), 1),
            (_, existing) => existing with
            {
                OccurrenceCount = existing.OccurrenceCount + 1,
                Timestamp = DateTime.Now
            });

        _logger.LogInformation("[ErrorLearning] Recorded: {Tool}.{Action} ({Count}x): {Error}",
            toolName, action, _errors[key].OccurrenceCount, error);

        // Persister périodiquement
        if (_errors.Count % 10 == 0)
            Save();

        return Task.CompletedTask;
    }

    public Task<bool> HasSeenErrorAsync(string toolName, string action, string error, CancellationToken ct = default)
    {
        var key = $"{toolName}:{action}:{NormalizeError(error)}";
        return Task.FromResult(_errors.ContainsKey(key));
    }

    public Task<IReadOnlyList<ErrorRecord>> GetSimilarErrorsAsync(string toolName, string action, int count = 10, CancellationToken ct = default)
    {
        var results = _errors.Values
            .Where(e => e.ToolName == toolName && e.Action == action)
            .OrderByDescending(e => e.OccurrenceCount)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<ErrorRecord>>(results);
    }

    public Task<IReadOnlyList<ErrorRecord>> GetRecentErrorsAsync(int count = 50, CancellationToken ct = default)
    {
        var results = _errors.Values
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<ErrorRecord>>(results);
    }

    public Task<string?> GetSuggestionAsync(string toolName, string action, string error, CancellationToken ct = default)
    {
        var key = $"{toolName}:{action}:{NormalizeError(error)}";
        if (_errors.TryGetValue(key, out var record))
            return Task.FromResult(record.Suggestion);

        return Task.FromResult(GetSuggestionForError(error));
    }

    private static string NormalizeError(string error)
    {
        // Normaliser les erreurs pour la déduplication
        return error.ToLowerInvariant()
            .Replace("timeout", "TIMEOUT")
            .Replace("not found", "NOT_FOUND")
            .Replace("access denied", "ACCESS_DENIED")
            .Replace("file not found", "FILE_NOT_FOUND")
            .Trim();
    }

    private static string? GetSuggestionForError(string error)
    {
        var lower = error.ToLowerInvariant();
        if (lower.Contains("timeout"))
            return "Augmenter le timeout ou diviser la tâche en sous-tâches plus petites.";
        if (lower.Contains("not found") || lower.Contains("non trouv"))
            return "Vérifier le nom/chemin ou utiliser find_process/find pour localiser.";
        if (lower.Contains("access denied") || lower.Contains("acc"))
            return "Vérifier les permissions ou exécuter en tant qu'administrateur.";
        if (lower.Contains("connection") || lower.Contains("connexion"))
            return "Vérifier la connexion réseau ou le pare-feu.";
        if (lower.Contains("memory") || lower.Contains("mémoire"))
            return "Le modèle est trop grand. Utiliser un modèle plus petit ou augmenter num_ctx.";
        return null;
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_errors.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_persistPath, json);
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;
            var json = File.ReadAllText(_persistPath);
            var records = JsonSerializer.Deserialize<List<ErrorRecord>>(json);
            if (records is not null)
                foreach (var r in records)
                    _errors[$"{r.ToolName}:{r.Action}:{NormalizeError(r.Error)}"] = r;
        }
        catch { }
    }
}
