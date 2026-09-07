using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Automation;

public interface ILoadTestService
{
    Task<LoadTestResult> RunAsync(string url, int requests = 1000, int concurrency = 50, CancellationToken ct = default);
}

public sealed class LoadTestService : ILoadTestService
{
    private readonly ILogger<LoadTestService> _logger;

    public LoadTestService(ILogger<LoadTestService> logger)
    {
        _logger = logger;
    }

    public async Task<LoadTestResult> RunAsync(string url, int requests = 1000, int concurrency = 50, CancellationToken ct = default)
    {
        var result = new LoadTestResult { Url = url, TotalRequests = requests };

        var latences = new List<double>();
        var statusCodes = new Dictionary<int, int>();
        var errors = new List<string>();
        int succeeded = 0;
        int failed = 0;
        var lockObj = new object();
        var stopwatch = Stopwatch.StartNew();

        var semaphore = new SemaphoreSlim(concurrency, concurrency);

        var tasks = Enumerable.Range(0, requests).Select(async i =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var sw = Stopwatch.StartNew();
                try
                {
                    var response = await client.GetAsync(url, ct);
                    sw.Stop();
                    lock (lockObj)
                    {
                        var code = (int)response.StatusCode;
                        statusCodes.TryGetValue(code, out var count);
                        statusCodes[code] = count + 1;

                        latences.Add(sw.Elapsed.TotalMilliseconds);
                        succeeded++;
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    lock (lockObj)
                    {
                        latences.Add(sw.Elapsed.TotalMilliseconds);
                        failed++;
                        if (errors.Count < 20)
                            errors.Add($"#{i}: {ex.Message}");
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        result.Succeeded = succeeded;
        result.Failed = failed;
        result.Elapsed = stopwatch.Elapsed;
        result.RequestsPerSecond = requests / stopwatch.Elapsed.TotalSeconds;
        result.StatusCodes = statusCodes;
        result.Errors = errors;

        if (latences.Count > 0)
        {
            latences.Sort();
            result.MinLatencyMs = latences[0];
            result.MaxLatencyMs = latences[^1];
            result.AverageLatencyMs = latences.Average();
            result.P50 = Percentile(latences, 50);
            result.P90 = Percentile(latences, 90);
            result.P95 = Percentile(latences, 95);
            result.P99 = Percentile(latences, 99);
        }

        result.Success = succeeded > 0 && failed == 0;

        _logger.LogInformation("[LoadTest] Terminé : {Succeeded}/{Total} réussis en {Elapsed} ({RPS} req/s)",
            succeeded, requests, stopwatch.Elapsed, result.RequestsPerSecond.ToString("F1"));

        return result;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}

public sealed class LoadTestResult
{
    public bool Success { get; set; }
    public string Url { get; set; } = "";
    public int TotalRequests { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public Dictionary<int, int> StatusCodes { get; set; } = new();
    public TimeSpan Elapsed { get; set; }
    public double RequestsPerSecond { get; set; }
    public double AverageLatencyMs { get; set; }
    public double MaxLatencyMs { get; set; }
    public double MinLatencyMs { get; set; }
    public double P50 { get; set; }
    public double P90 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
