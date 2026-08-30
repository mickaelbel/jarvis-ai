namespace JarvisAI.Application.Security;

/// <summary>
/// Enregistre toutes les actions de l'agent pour audit et révision.
/// </summary>
public interface IAuditLogService
{
    Task LogAsync(AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int count = 50, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetByToolAsync(string toolName, int count = 50, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetErrorsAsync(int count = 50, CancellationToken ct = default);
    Task<AuditStats> GetStatsAsync(CancellationToken ct = default);
}

public sealed record AuditEntry(
    Guid Id,
    DateTime Timestamp,
    string ToolName,
    string Action,
    string Parameters,
    bool Success,
    string? Output,
    string? Error,
    long DurationMs,
    string? UserMessage);

public sealed record AuditStats(
    int TotalActions,
    int SuccessfulActions,
    int FailedActions,
    Dictionary<string, int> ActionsByTool,
    Dictionary<string, int> ErrorsByType,
    double AvgDurationMs);
