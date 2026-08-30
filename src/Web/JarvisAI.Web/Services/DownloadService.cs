using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public sealed class DownloadService : IDownloadService
{
    private readonly ILogger<DownloadService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _downloadDir;
    private static readonly List<DownloadInfo> _downloads = new();

    public DownloadService(ILogger<DownloadService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _downloadDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "JarvisAI");
        Directory.CreateDirectory(_downloadDir);
    }

    public async Task<DownloadResult> DownloadAsync(string url, string? fileName = null, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[Download] Starting: {Url}", url);

            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // Déterminer le nom du fichier
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(fileName))
                    fileName = $"download_{DateTime.Now:yyyyMMdd_HHmmss}";
            }

            var filePath = Path.Combine(_downloadDir, fileName);

            // Télécharger
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = File.Create(filePath);
            await stream.CopyToAsync(fileStream, ct);
            await fileStream.FlushAsync(ct);

            var fileInfo = new FileInfo(filePath);
            var info = new DownloadInfo(fileName, url, fileInfo.Length, DateTime.Now, true);

            lock (_downloads)
            {
                _downloads.Add(info);
            }

            _logger.LogInformation("[Download] Completed: {File} ({Size} bytes)", fileName, fileInfo.Length);

            return new DownloadResult(true, filePath, fileInfo.Length, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Download] Failed: {Url}", url);
            return new DownloadResult(false, "", 0, ex.Message);
        }
    }

    public Task<IReadOnlyList<DownloadInfo>> GetDownloadsAsync(CancellationToken ct = default)
    {
        lock (_downloads)
        {
            return Task.FromResult<IReadOnlyList<DownloadInfo>>(_downloads.ToList());
        }
    }
}
