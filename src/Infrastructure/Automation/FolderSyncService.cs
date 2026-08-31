using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IFolderSyncService
{
    Task<SyncResult> SyncFoldersAsync(string source, string destination, SyncMode mode = SyncMode.Mirror, CancellationToken ct = default);
    Task<SyncResult> GetDifferencesAsync(string source, string destination, CancellationToken ct = default);
}

public enum SyncMode
{
    Mirror,    // Destination matches source exactly
    Backup,    // Copy new/modified files only
    BiDirectional // Sync both ways
}

public sealed class FolderSyncService : IFolderSyncService
{
    private readonly ILogger<FolderSyncService> _logger;

    public FolderSyncService(ILogger<FolderSyncService> logger)
    {
        _logger = logger;
    }

    public async Task<SyncResult> SyncFoldersAsync(string source, string destination, SyncMode mode = SyncMode.Mirror, CancellationToken ct = default)
    {
        var result = new SyncResult { Source = source, Destination = destination, Mode = mode };

        if (!Directory.Exists(source))
        {
            result.ErrorMessage = $"Source not found: {source}";
            return result;
        }

        if (!Directory.Exists(destination))
            Directory.CreateDirectory(destination);

        var sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var destFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);

        // Copy new/modified files
        foreach (var srcFile in sourceFiles)
        {
            if (ct.IsCancellationRequested) break;

            var relativePath = Path.GetRelativePath(source, srcFile);
            var destFile = Path.Combine(destination, relativePath);
            var destDir = Path.GetDirectoryName(destFile);

            if (destDir is not null && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            var srcInfo = new FileInfo(srcFile);
            var destInfo = File.Exists(destFile) ? new FileInfo(destFile) : null;

            if (destInfo is null || srcInfo.LastWriteTime > destInfo.LastWriteTime)
            {
                File.Copy(srcFile, destFile, true);
                result.FilesCopied++;
                _logger.LogDebug("[Sync] Copied: {File}", relativePath);
            }
        }

        // Delete files in destination not in source (mirror mode)
        if (mode == SyncMode.Mirror)
        {
            foreach (var destFile in destFiles)
            {
                if (ct.IsCancellationRequested) break;

                var relativePath = Path.GetRelativePath(destination, destFile);
                var srcFile = Path.Combine(source, relativePath);

                if (!File.Exists(srcFile))
                {
                    File.Delete(destFile);
                    result.FilesDeleted++;
                    _logger.LogDebug("[Sync] Deleted: {File}", relativePath);
                }
            }
        }

        result.Success = true;
        _logger.LogInformation("[Sync] Completed: {Copied} copied, {Deleted} deleted", result.FilesCopied, result.FilesDeleted);
        return result;
    }

    public Task<SyncResult> GetDifferencesAsync(string source, string destination, CancellationToken ct = default)
    {
        var result = new SyncResult { Source = source, Destination = destination };

        if (!Directory.Exists(source) || !Directory.Exists(destination))
            return Task.FromResult(result);

        var sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f)).ToHashSet();
        var destFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(destination, f)).ToHashSet();

        result.FilesOnlyInSource = sourceFiles.Except(destFiles).ToList();
        result.FilesOnlyInDestination = destFiles.Except(sourceFiles).ToList();
        result.Success = true;

        return Task.FromResult(result);
    }
}

public sealed class SyncResult
{
    public bool Success { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public SyncMode Mode { get; set; }
    public int FilesCopied { get; set; }
    public int FilesDeleted { get; set; }
    public List<string> FilesOnlyInSource { get; set; } = new();
    public List<string> FilesOnlyInDestination { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
