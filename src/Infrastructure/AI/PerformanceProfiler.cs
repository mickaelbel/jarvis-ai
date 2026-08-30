using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.AI;

public interface IPerformanceProfiler
{
    Task<ProfileResult> ProfileMethodAsync(string methodName, Func<Task> method, CancellationToken ct = default);
    ProfileResult ProfileMethod(string methodName, Action method);
    IReadOnlyList<ProfileResult> GetProfileHistory();
    MemorySnapshot TakeMemorySnapshot();
    IReadOnlyList<MemoryLeakCandidate> DetectMemoryLeaks();
}

public sealed class PerformanceProfiler : IPerformanceProfiler
{
    private readonly ILogger<PerformanceProfiler> _logger;
    private readonly List<ProfileResult> _history = new();
    private readonly List<MemorySnapshot> _snapshots = new();

    public PerformanceProfiler(ILogger<PerformanceProfiler> logger)
    {
        _logger = logger;
    }

    public async Task<ProfileResult> ProfileMethodAsync(string methodName, Func<Task> method, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var startMemory = GC.GetTotalMemory(true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        Exception? error = null;
        try
        {
            await method();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        sw.Stop();
        var endMemory = GC.GetTotalMemory(false);

        var result = new ProfileResult
        {
            MethodName = methodName,
            DurationMs = sw.ElapsedMilliseconds,
            MemoryBeforeBytes = startMemory,
            MemoryAfterBytes = endMemory,
            MemoryDeltaBytes = endMemory - startMemory,
            Gen0Collections = GC.CollectionCount(0) - gen0Before,
            Gen1Collections = GC.CollectionCount(1) - gen1Before,
            Gen2Collections = GC.CollectionCount(2) - gen2Before,
            Success = error is null,
            ErrorMessage = error?.Message,
            ProfiledAt = DateTime.UtcNow
        };

        _history.Add(result);
        _logger.LogInformation("[Profile] {Method}: {Duration}ms, {Memory}KB, GC: {G0}/{G1}/{G2}",
            methodName, result.DurationMs, result.MemoryDeltaBytes / 1024,
            result.Gen0Collections, result.Gen1Collections, result.Gen2Collections);

        return result;
    }

    public ProfileResult ProfileMethod(string methodName, Action method)
    {
        var sw = Stopwatch.StartNew();
        var startMemory = GC.GetTotalMemory(true);

        Exception? error = null;
        try
        {
            method();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        sw.Stop();
        var endMemory = GC.GetTotalMemory(false);

        var result = new ProfileResult
        {
            MethodName = methodName,
            DurationMs = sw.ElapsedMilliseconds,
            MemoryBeforeBytes = startMemory,
            MemoryAfterBytes = endMemory,
            MemoryDeltaBytes = endMemory - startMemory,
            Success = error is null,
            ErrorMessage = error?.Message,
            ProfiledAt = DateTime.UtcNow
        };

        _history.Add(result);
        return result;
    }

    public IReadOnlyList<ProfileResult> GetProfileHistory() => _history.ToList();

    public MemorySnapshot TakeMemorySnapshot()
    {
        var process = Process.GetCurrentProcess();
        var snapshot = new MemorySnapshot
        {
            Timestamp = DateTime.UtcNow,
            WorkingSet64 = process.WorkingSet64,
            PrivateMemorySize64 = process.PrivateMemorySize64,
            VirtualMemorySize64 = process.VirtualMemorySize64,
            GcTotalMemory = GC.GetTotalMemory(false),
            Gen0Count = GC.CollectionCount(0),
            Gen1Count = GC.CollectionCount(1),
            Gen2Count = GC.CollectionCount(2),
            HandleCount = process.HandleCount,
            ThreadCount = process.Threads.Count
        };

        _snapshots.Add(snapshot);

        // Keep only last 100 snapshots
        if (_snapshots.Count > 100)
            _snapshots.RemoveRange(0, _snapshots.Count - 100);

        return snapshot;
    }

    public IReadOnlyList<MemoryLeakCandidate> DetectMemoryLeaks()
    {
        var candidates = new List<MemoryLeakCandidate>();

        if (_snapshots.Count >= 2)
        {
            var recent = _snapshots.TakeLast(10).ToList();
            var memoryGrowth = recent.Last().GcTotalMemory - recent.First().GcTotalMemory;
            var timeSpan = recent.Last().Timestamp - recent.First().Timestamp;

            if (memoryGrowth > 10 * 1024 * 1024 && timeSpan.TotalMinutes > 5) // >10MB growth in 5 min
            {
                candidates.Add(new MemoryLeakCandidate
                {
                    Type = "Heap Growth",
                    Description = $"Mémoire heap augmentée de {memoryGrowth / 1024 / 1024:N1} MB en {timeSpan.TotalMinutes:F1} minutes",
                    Severity = LeakSeverity.High,
                    Recommendation = "Vérifier les événements non désabonnés, collections static, caches non limités"
                });
            }

            var handleGrowth = recent.Last().HandleCount - recent.First().HandleCount;
            if (handleGrowth > 1000)
            {
                candidates.Add(new MemoryLeakCandidate
                {
                    Type = "Handle Leak",
                    Description = $"{handleGrowth} handles créés en {timeSpan.TotalMinutes:F1} minutes",
                    Severity = LeakSeverity.Medium,
                    Recommendation = "Vérifier les Process, FileStream, HttpClient non disposés"
                });
            }

            var threadGrowth = recent.Last().ThreadCount - recent.First().ThreadCount;
            if (threadGrowth > 50)
            {
                candidates.Add(new MemoryLeakCandidate
                {
                    Type = "Thread Leak",
                    Description = $"{threadGrowth} threads créés en {timeSpan.TotalMinutes:F1} minutes",
                    Severity = LeakSeverity.Medium,
                    Recommendation = "Vérifier les Task.Run sans limitation, ThreadPool overflow"
                });
            }
        }

        return candidates;
    }
}

public sealed class ProfileResult
{
    public string MethodName { get; set; } = "";
    public long DurationMs { get; set; }
    public long MemoryBeforeBytes { get; set; }
    public long MemoryAfterBytes { get; set; }
    public long MemoryDeltaBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ProfiledAt { get; set; }
}

public sealed class MemorySnapshot
{
    public DateTime Timestamp { get; set; }
    public long WorkingSet64 { get; set; }
    public long PrivateMemorySize64 { get; set; }
    public long VirtualMemorySize64 { get; set; }
    public long GcTotalMemory { get; set; }
    public int Gen0Count { get; set; }
    public int Gen1Count { get; set; }
    public int Gen2Count { get; set; }
    public int HandleCount { get; set; }
    public int ThreadCount { get; set; }
}

public sealed class MemoryLeakCandidate
{
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public LeakSeverity Severity { get; set; }
    public string Recommendation { get; set; } = "";
}

public enum LeakSeverity { Low, Medium, High, Critical }
