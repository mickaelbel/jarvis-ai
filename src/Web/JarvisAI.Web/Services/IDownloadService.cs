namespace JarvisAI.Web.Services;

/// <summary>
/// Téléchargement automatique de fichiers via le navigateur.
/// </summary>
public interface IDownloadService
{
    Task<DownloadResult> DownloadAsync(string url, string? fileName = null, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadInfo>> GetDownloadsAsync(CancellationToken ct = default);
}

public sealed record DownloadResult(
    bool Success,
    string FilePath,
    long SizeBytes,
    string? Error);

public sealed record DownloadInfo(
    string FileName,
    string Url,
    long SizeBytes,
    DateTime DownloadedAt,
    bool IsComplete);
