using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Automation;

public interface ISiteMonitorService
{
    Task<SiteStatus> CheckSiteAsync(string url, CancellationToken ct = default);
    Task<List<SiteStatus>> CheckMultipleSitesAsync(IEnumerable<string> urls, CancellationToken ct = default);
    Task<MonitoringResult> StartMonitoringAsync(IEnumerable<string> urls, int intervalSeconds = 300, CancellationToken ct = default);
    Task<List<Alert>> GetAlertsAsync();
}

public sealed class SiteMonitorService : ISiteMonitorService
{
    private readonly ILogger<SiteMonitorService> _logger;
    private readonly HttpClient _httpClient;
    private readonly List<Alert> _alerts = new();
    private readonly string _storagePath;

    public SiteMonitorService(ILogger<SiteMonitorService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _storagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI", "site_alerts.json");
    }

    public async Task<SiteStatus> CheckSiteAsync(string url, CancellationToken ct = default)
    {
        var status = new SiteStatus { Url = url, CheckedAt = DateTime.UtcNow };

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(url, ct);
            sw.Stop();

            status.StatusCode = (int)response.StatusCode;
            status.IsSuccess = response.IsSuccessStatusCode;
            status.ResponseTimeMs = sw.ElapsedMilliseconds;
            status.ContentType = response.Content.Headers.ContentType?.MediaType;
        }
        catch (TaskCanceledException)
        {
            status.IsSuccess = false;
            status.ErrorMessage = "Timeout";
        }
        catch (Exception ex)
        {
            status.IsSuccess = false;
            status.ErrorMessage = ex.Message;
        }

        return status;
    }

    public async Task<List<SiteStatus>> CheckMultipleSitesAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        var results = new List<SiteStatus>();
        foreach (var url in urls)
        {
            if (ct.IsCancellationRequested) break;
            results.Add(await CheckSiteAsync(url, ct));
        }
        return results;
    }

    public async Task<MonitoringResult> StartMonitoringAsync(IEnumerable<string> urls, int intervalSeconds = 300, CancellationToken ct = default)
    {
        var result = new MonitoringResult { StartedAt = DateTime.UtcNow };
        var urlList = urls.ToList();

        while (!ct.IsCancellationRequested)
        {
            foreach (var url in urlList)
            {
                var status = await CheckSiteAsync(url, ct);
                result.Checks.Add(status);

                if (!status.IsSuccess)
                {
                    _alerts.Add(new Alert
                    {
                        Url = url,
                        Message = $"Site down: {status.ErrorMessage ?? status.StatusCode.ToString()}",
                        OccurredAt = DateTime.UtcNow
                    });
                    _logger.LogWarning("[SiteMonitor] DOWN: {Url}", url);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }

        result.CompletedAt = DateTime.UtcNow;
        return result;
    }

    public Task<List<Alert>> GetAlertsAsync()
    {
        return Task.FromResult(_alerts.OrderByDescending(a => a.OccurredAt).ToList());
    }
}

public sealed class SiteStatus
{
    public string Url { get; set; } = "";
    public bool IsSuccess { get; set; }
    public int StatusCode { get; set; }
    public long ResponseTimeMs { get; set; }
    public string? ContentType { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CheckedAt { get; set; }
}

public sealed class MonitoringResult
{
    public List<SiteStatus> Checks { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class Alert
{
    public string Url { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime OccurredAt { get; set; }
}
