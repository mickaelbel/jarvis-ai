using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace JarvisAI.Infrastructure.Automation;

public interface IFileDeduplicationService
{
    Task<List<DuplicateGroup>> FindDuplicatesAsync(string path, bool recursive = true, CancellationToken ct = default);
    Task<int> RemoveDuplicatesAsync(IEnumerable<DuplicateGroup> groups, DuplicateRemovalStrategy strategy = DuplicateRemovalStrategy.KeepOldest, CancellationToken ct = default);
    Task<DeduplicationReport> GetReportAsync(string path, CancellationToken ct = default);
}

public enum DuplicateRemovalStrategy
{
    KeepOldest,
    KeepNewest,
    KeepLargest,
    KeepSmallest,
    KeepFirst
}

public sealed class FileDeduplicationService : IFileDeduplicationService
{
    private readonly ILogger<FileDeduplicationService> _logger;

    public FileDeduplicationService(ILogger<FileDeduplicationService> logger)
    {
        _logger = logger;
    }

    public async Task<List<DuplicateGroup>> FindDuplicatesAsync(string path, bool recursive = true, CancellationToken ct = default)
    {
        var groups = new Dictionary<string, DuplicateGroup>();
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        await Task.Run(() =>
        {
            foreach (var file in Directory.GetFiles(path, "*", searchOption))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var hash = ComputeFileHash(file);
                    var info = new FileInfo(file);

                    if (!groups.ContainsKey(hash))
                        groups[hash] = new DuplicateGroup { Hash = hash };

                    groups[hash].Files.Add(new DuplicateFileInfo
                    {
                        Path = file,
                        SizeBytes = info.Length,
                        LastModified = info.LastWriteTime,
                        Created = info.CreationTime
                    });
                }
                catch { }
            }
        }, ct);

        return groups.Values.Where(g => g.Files.Count > 1).OrderByDescending(g => g.WastedBytes).ToList();
    }

    public async Task<int> RemoveDuplicatesAsync(IEnumerable<DuplicateGroup> groups, DuplicateRemovalStrategy strategy = DuplicateRemovalStrategy.KeepOldest, CancellationToken ct = default)
    {
        var removed = 0;

        foreach (var group in groups)
        {
            var filesToKeep = strategy switch
            {
                DuplicateRemovalStrategy.KeepOldest => group.Files.OrderBy(f => f.Created).Take(1),
                DuplicateRemovalStrategy.KeepNewest => group.Files.OrderByDescending(f => f.Created).Take(1),
                DuplicateRemovalStrategy.KeepLargest => group.Files.OrderByDescending(f => f.SizeBytes).Take(1),
                DuplicateRemovalStrategy.KeepSmallest => group.Files.OrderBy(f => f.SizeBytes).Take(1),
                DuplicateRemovalStrategy.KeepFirst => group.Files.Take(1),
                _ => group.Files.Take(1)
            };

            var keepPath = filesToKeep.FirstOrDefault()?.Path;
            var filesToDelete = group.Files.Where(f => f.Path != keepPath);

            foreach (var file in filesToDelete)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    File.Delete(file.Path);
                    removed++;
                    _logger.LogDebug("[Dedup] Removed: {Path}", file.Path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Dedup] Failed to delete: {Path}", file.Path);
                }
            }

            await Task.Delay(10, ct);
        }

        _logger.LogInformation("[Dedup] Removed {Count} duplicate files", removed);
        return removed;
    }

    public async Task<DeduplicationReport> GetReportAsync(string path, CancellationToken ct = default)
    {
        var duplicates = await FindDuplicatesAsync(path, true, ct);

        return new DeduplicationReport
        {
            Path = path,
            TotalGroups = duplicates.Count,
            TotalDuplicates = duplicates.Sum(g => g.Files.Count - 1),
            WastedBytes = duplicates.Sum(g => g.WastedBytes),
            ScannedAt = DateTime.UtcNow
        };
    }

    private static string ComputeFileHash(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToBase64String(hash);
    }
}

public sealed class DuplicateGroup
{
    public string Hash { get; set; } = "";
    public List<DuplicateFileInfo> Files { get; set; } = new();
    public long WastedBytes => Files.Count > 1 ? Files.Skip(1).Sum(f => f.SizeBytes) : 0;
}

public sealed class DuplicateFileInfo
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public DateTime Created { get; set; }
}

public sealed class DeduplicationReport
{
    public string Path { get; set; } = "";
    public int TotalGroups { get; set; }
    public int TotalDuplicates { get; set; }
    public long WastedBytes { get; set; }
    public DateTime ScannedAt { get; set; }
}
