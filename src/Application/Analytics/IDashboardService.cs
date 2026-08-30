namespace JarvisAI.Application.Analytics;

/// <summary>
/// Dashboard d'analytics pour Jarvis AI.
/// </summary>
public interface IDashboardService
{
    Task<DashboardData> GetDataAsync(CancellationToken ct = default);
    Task<DashboardData> GetDataAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

public sealed record DashboardData(
    int TotalSessions,
    int TotalMessages,
    int TotalToolCalls,
    double AvgResponseTimeMs,
    Dictionary<string, int> ToolUsage,
    Dictionary<string, int> ModelUsage,
    Dictionary<string, int> ErrorTypes,
    List<HourlyActivity> ActivityByHour,
    List<DailyActivity> ActivityByDay,
    int TotalTokensUsed,
    double EstimatedCostUsd);

public sealed record HourlyActivity(int Hour, int MessageCount, int ToolCalls);
public sealed record DailyActivity(DateTime Date, int MessageCount, int ToolCalls, int Errors);
