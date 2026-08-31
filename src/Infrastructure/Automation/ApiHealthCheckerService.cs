using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IApiHealthCheckerService
{
    Task<List<ApiHealthResult>> CheckEndpointsAsync(IEnumerable<string> urls, CancellationToken ct = default);
    Task<ApiHealthResult> CheckEndpointAsync(string url, CancellationToken ct = default);
    Task<MonitoringReport> MonitorAsync(IEnumerable<string> urls, int intervalSeconds = 60, int durationMinutes = 5, CancellationToken ct = default);
}

public sealed class ApiHealthCheckerService : IApiHealthCheckerService
{
    private readonly ILogger<ApiHealthCheckerService> _logger;
    private readonly HttpClient _httpClient;

    public ApiHealthCheckerService(ILogger<ApiHealthCheckerService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<List<ApiHealthResult>> CheckEndpointsAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        var results = new List<ApiHealthResult>();
        foreach (var url in urls)
        {
            if (ct.IsCancellationRequested) break;
            results.Add(await CheckEndpointAsync(url, ct));
        }
        return results;
    }

    public async Task<ApiHealthResult> CheckEndpointAsync(string url, CancellationToken ct = default)
    {
        var result = new ApiHealthResult { Url = url, CheckedAt = DateTime.UtcNow };

        try
        {
            var sw = Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(url, ct);
            sw.Stop();

            result.StatusCode = (int)response.StatusCode;
            result.IsSuccess = response.IsSuccessStatusCode;
            result.ResponseTimeMs = sw.ElapsedMilliseconds;
            result.ContentLength = response.Content.Headers.ContentLength ?? 0;
        }
        catch (TaskCanceledException)
        {
            result.ErrorMessage = "Timeout";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async Task<MonitoringReport> MonitorAsync(IEnumerable<string> urls, int intervalSeconds = 60, int durationMinutes = 5, CancellationToken ct = default)
    {
        var report = new MonitoringReport { StartedAt = DateTime.UtcNow };
        var urlList = urls.ToList();
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < TimeSpan.FromMinutes(durationMinutes) && !ct.IsCancellationRequested)
        {
            foreach (var url in urlList)
            {
                var result = await CheckEndpointAsync(url, ct);
                report.Checks.Add(result);

                if (!result.IsSuccess)
                {
                    report.Alerts.Add($"{url}: {result.ErrorMessage ?? result.StatusCode.ToString()}");
                    _logger.LogWarning("[ApiHealth] DOWN: {Url}", url);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }

        report.CompletedAt = DateTime.UtcNow;
        report.Success = true;
        return report;
    }
}

public sealed class ApiHealthResult
{
    public string Url { get; set; } = "";
    public bool IsSuccess { get; set; }
    public int StatusCode { get; set; }
    public long ResponseTimeMs { get; set; }
    public long ContentLength { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CheckedAt { get; set; }
}

public sealed class MonitoringReport
{
    public bool Success { get; set; }
    public List<ApiHealthResult> Checks { get; set; } = new();
    public List<string> Alerts { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
