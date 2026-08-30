using JarvisAI.Application.Analytics;
using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Infrastructure.Analytics;

public sealed class DashboardService : IDashboardService
{
    private readonly ILogger<DashboardService> _logger;
    private readonly IAuditLogService _audit;
    private readonly IErrorLearningService _errorLearning;
    private static readonly ConcurrentBag<SessionMetrics> _sessions = new();

    public DashboardService(
        ILogger<DashboardService> logger,
        IAuditLogService audit,
        IErrorLearningService errorLearning)
    {
        _logger = logger;
        _audit = audit;
        _errorLearning = errorLearning;
    }

    public async Task<DashboardData> GetDataAsync(CancellationToken ct = default)
    {
        return await GetDataAsync(DateTime.MinValue, DateTime.Now, ct);
    }

    public async Task<DashboardData> GetDataAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var entries = await _audit.GetRecentAsync(10000, ct);
        var filtered = entries.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList();
        var errors = await _errorLearning.GetRecentErrorsAsync(10000, ct);

        var toolUsage = filtered
            .GroupBy(e => e.ToolName)
            .ToDictionary(g => g.Key, g => g.Count());

        var errorTypes = errors
            .GroupBy(e => e.Error.Length > 50 ? e.Error[..50] : e.Error)
            .ToDictionary(g => g.Key, g => g.Count());

        var hourly = Enumerable.Range(0, 24)
            .Select(h => new HourlyActivity(
                h,
                filtered.Count(e => e.Timestamp.Hour == h),
                filtered.Count(e => e.Timestamp.Hour == h)))
            .ToList();

        var daily = filtered
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new DailyActivity(
                g.Key,
                g.Count(),
                g.Count(),
                g.Count(e => !e.Success)))
            .OrderBy(d => d.Date)
            .ToList();

        var avgResponseTime = filtered.Count > 0
            ? filtered.Average(e => e.DurationMs)
            : 0;

        var totalTokens = filtered.Count * 500; // Estimation
        var estimatedCost = totalTokens * 0.000001; // Estimation

        return new DashboardData(
            _sessions.Count,
            filtered.Count,
            filtered.Count,
            avgResponseTime,
            toolUsage,
            new Dictionary<string, int>(),
            errorTypes,
            hourly,
            daily,
            totalTokens,
            estimatedCost);
    }

    public static void RecordSession()
    {
        _sessions.Add(new SessionMetrics(DateTime.Now));
    }
}

internal sealed record SessionMetrics(DateTime StartTime);
