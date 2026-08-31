using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Infrastructure.Automation;

public interface IDownloadManagerService
{
    Task<DownloadResult> DownloadFileAsync(string url, string? outputPath = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
    Task<DownloadResult> DownloadBatchAsync(IEnumerable<string> urls, string? outputDir = null, int maxParallel = 5, CancellationToken ct = default);
    Task<List<DownloadInfo>> GetActiveDownloads();
    Task<bool> CancelDownloadAsync(string downloadId);
}

public sealed class DownloadManagerService : IDownloadManagerService
{
    private readonly ILogger<DownloadManagerService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeDownloads = new();

    public DownloadManagerService(ILogger<DownloadManagerService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public async Task<DownloadResult> DownloadFileAsync(string url, string? outputPath = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new DownloadResult { Url = url };
        var downloadId = Guid.NewGuid().ToString("N")[..8];
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeDownloads[downloadId] = cts;

        try
        {
            var fileName = outputPath ?? Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(fileName)) fileName = "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var outputDir = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            var filePath = Path.Combine(outputDir, fileName);

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var totalBytesRead = 0L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192);

            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                totalBytesRead += bytesRead;

                progress?.Report(new DownloadProgress
                {
                    BytesReceived = totalBytesRead,
                    TotalBytesToReceive = totalBytes,
                    Percentage = totalBytes > 0 ? (int)(totalBytesRead * 100 / totalBytes) : 0
                });
            }

            result.Success = true;
            result.OutputPath = filePath;
            result.FileSizeBytes = totalBytesRead;
            _logger.LogInformation("[Download] Completed: {Url} → {Path}", url, filePath);
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Download cancelled";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogWarning(ex, "[Download] Failed: {Url}", url);
        }
        finally
        {
            _activeDownloads.TryRemove(downloadId, out _);
        }

        return result;
    }

    public async Task<DownloadResult> DownloadBatchAsync(IEnumerable<string> urls, string? outputDir = null, int maxParallel = 5, CancellationToken ct = default)
    {
        var result = new DownloadResult();
        var dir = outputDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct };

        await Parallel.ForEachAsync(urls, parallelOptions, async (url, token) =>
        {
            var downloadResult = await DownloadFileAsync(url, Path.Combine(dir, Path.GetFileName(new Uri(url).AbsolutePath)), null, token);
            if (downloadResult.Success) result.FilesDownloaded++;
            else result.Errors.Add(url);
        });

        result.Success = true;
        _logger.LogInformation("[Download] Batch completed: {Count} files", result.FilesDownloaded);
        return result;
    }

    public Task<List<DownloadInfo>> GetActiveDownloads()
    {
        return Task.FromResult(_activeDownloads.Keys.Select(id => new DownloadInfo { Id = id }).ToList());
    }

    public Task<bool> CancelDownloadAsync(string downloadId)
    {
        if (_activeDownloads.TryRemove(downloadId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}

public sealed class DownloadResult
{
    public bool Success { get; set; }
    public string Url { get; set; } = "";
    public string? OutputPath { get; set; }
    public long FileSizeBytes { get; set; }
    public int FilesDownloaded { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public sealed class DownloadProgress
{
    public long BytesReceived { get; set; }
    public long TotalBytesToReceive { get; set; }
    public int Percentage { get; set; }
}

public sealed class DownloadInfo
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}
