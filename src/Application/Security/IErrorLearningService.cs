namespace JarvisAI.Application.Security;

/// <summary>
/// Apprend des erreurs passées pour ne plus les répéter.
/// </summary>
public interface IErrorLearningService
{
    Task RecordErrorAsync(string toolName, string action, string error, string? context = null, CancellationToken ct = default);
    Task<bool> HasSeenErrorAsync(string toolName, string action, string error, CancellationToken ct = default);
    Task<IReadOnlyList<ErrorRecord>> GetSimilarErrorsAsync(string toolName, string action, int count = 10, CancellationToken ct = default);
    Task<IReadOnlyList<ErrorRecord>> GetRecentErrorsAsync(int count = 50, CancellationToken ct = default);
    Task<string?> GetSuggestionAsync(string toolName, string action, string error, CancellationToken ct = default);
}

public sealed record ErrorRecord(
    Guid Id,
    DateTime Timestamp,
    string ToolName,
    string Action,
    string Error,
    string? Context,
    string? Suggestion,
    int OccurrenceCount);
