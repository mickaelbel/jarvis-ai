using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface ISystemCleanupService
{
    Task<CleanupResult> CleanupTempFilesAsync(CancellationToken ct = default);
    Task<CleanupResult> CleanupBrowserCacheAsync(CancellationToken ct = default);
    Task<CleanupResult> CleanupRecycleBinAsync(CancellationToken ct = default);
    Task<CleanupResult> FullCleanupAsync(CancellationToken ct = default);
    Task<DiskUsageReport> GetDiskUsageAsync(CancellationToken ct = default);
    Task<List<LargeFile>> FindLargeFilesAsync(string path, long minSizeMb = 100, int limit = 50, CancellationToken ct = default);
}

public sealed class SystemCleanupService : ISystemCleanupService
{
    private readonly ILogger<SystemCleanupService> _logger;

    public SystemCleanupService(ILogger<SystemCleanupService> logger)
    {
        _logger = logger;
    }

    public async Task<CleanupResult> CleanupTempFilesAsync(CancellationToken ct = default)
    {
        var result = new CleanupResult { Operation = "Temp Files Cleanup" };
        var tempPaths = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer", "thumbcache_*")
        };

        foreach (var tempPath in tempPaths)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var pattern = Path.GetFileName(tempPath);
                var dir = Path.GetDirectoryName(tempPath) ?? tempPath;

                if (pattern.Contains('*'))
                {
                    // Handle wildcard patterns
                    var dirPath = Path.GetDirectoryName(tempPath) ?? Path.GetTempPath();
                    var searchPattern = Path.GetFileName(tempPath);
                    foreach (var file in Directory.GetFiles(dirPath, searchPattern))
                    {
                        try
                        {
                            var size = new FileInfo(file).Length;
                            File.Delete(file);
                            result.FreedBytes += size;
                            result.FilesDeleted++;
                        }
                        catch { }
                    }
                }
                else if (Directory.Exists(tempPath))
                {
                    foreach (var file in Directory.GetFiles(tempPath, "*", SearchOption.AllDirectories))
                    {
                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            var info = new FileInfo(file);
                            if (DateTime.Now - info.LastWriteTime > TimeSpan.FromHours(24))
                            {
                                var size = info.Length;
                                File.Delete(file);
                                result.FreedBytes += size;
                                result.FilesDeleted++;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Cleanup] Failed to clean: {Path}", tempPath);
            }
        }

        result.Success = true;
        _logger.LogInformation("[Cleanup] Temp files: deleted {Count}, freed {Size:N0} bytes",
            result.FilesDeleted, result.FreedBytes);
        return result;
    }

    public async Task<CleanupResult> CleanupBrowserCacheAsync(CancellationToken ct = default)
    {
        var result = new CleanupResult { Operation = "Browser Cache Cleanup" };

        var cachePaths = new[]
        {
            // Chrome
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Cache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Code Cache"),
            // Firefox
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mozilla", "Firefox", "Profiles"),
            // Edge
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Default", "Cache"),
        };

        foreach (var cachePath in cachePaths)
        {
            if (ct.IsCancellationRequested || !Directory.Exists(cachePath)) continue;

            try
            {
                foreach (var file in Directory.GetFiles(cachePath, "*", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var info = new FileInfo(file);
                        var size = info.Length;
                        File.Delete(file);
                        result.FreedBytes += size;
                        result.FilesDeleted++;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Cleanup] Failed to clean browser cache: {Path}", cachePath);
            }
        }

        result.Success = true;
        _logger.LogInformation("[Cleanup] Browser cache: deleted {Count}, freed {Size:N0} bytes",
            result.FilesDeleted, result.FreedBytes);
        return result;
    }

    public async Task<CleanupResult> CleanupRecycleBinAsync(CancellationToken ct = default)
    {
        var result = new CleanupResult { Operation = "Recycle Bin Cleanup" };

        try
        {
            // Use PowerShell to empty recycle bin
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-Command \"Clear-RecycleBin -Force -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync(ct);
                result.Success = true;
                _logger.LogInformation("[Cleanup] Recycle bin emptied");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Cleanup] Failed to empty recycle bin");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async Task<CleanupResult> FullCleanupAsync(CancellationToken ct = default)
    {
        var result = new CleanupResult { Operation = "Full Cleanup" };
        var startTime = DateTime.UtcNow;

        var tempResult = await CleanupTempFilesAsync(ct);
        result.FilesDeleted += tempResult.FilesDeleted;
        result.FreedBytes += tempResult.FreedBytes;

        var cacheResult = await CleanupBrowserCacheAsync(ct);
        result.FilesDeleted += cacheResult.FilesDeleted;
        result.FreedBytes += cacheResult.FreedBytes;

        var recycleResult = await CleanupRecycleBinAsync(ct);
        if (recycleResult.Success) result.FilesDeleted++;

        result.Success = true;
        result.Duration = DateTime.UtcNow - startTime;

        _logger.LogInformation("[Cleanup] Full cleanup: deleted {Count} files, freed {Size:N0} bytes in {Ms}ms",
            result.FilesDeleted, result.FreedBytes, result.Duration?.TotalMilliseconds ?? 0);

        return result;
    }

    public async Task<DiskUsageReport> GetDiskUsageAsync(CancellationToken ct = default)
    {
        var report = new DiskUsageReport();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;

            report.Drives.Add(new DriveInfoModel
            {
                Name = drive.Name,
                Label = drive.VolumeLabel,
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.AvailableFreeSpace,
                UsedBytes = drive.TotalSize - drive.AvailableFreeSpace,
                UsagePercent = (int)((drive.TotalSize - drive.AvailableFreeSpace) * 100.0 / drive.TotalSize)
            });
        }

        // Find largest folders in user profile
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var largeFolders = new[] { "Desktop", "Documents", "Downloads", "Pictures", "Videos" };

        foreach (var folder in largeFolders)
        {
            var path = Path.Combine(userProfile, folder);
            if (Directory.Exists(path))
            {
                try
                {
                    var size = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                        .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });

                    report.TopFolders.Add(new FolderUsage
                    {
                        Path = path,
                        SizeBytes = size
                    });
                }
                catch { }
            }
        }

        report.TopFolders = report.TopFolders.OrderByDescending(f => f.SizeBytes).ToList();
        return report;
    }

    public async Task<List<LargeFile>> FindLargeFilesAsync(string path, long minSizeMb = 100, int limit = 50, CancellationToken ct = default)
    {
        var largeFiles = new List<LargeFile>();
        var minSizeBytes = minSizeMb * 1024 * 1024;

        if (!Directory.Exists(path))
            return largeFiles;

        await Task.Run(() =>
        {
            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length >= minSizeBytes)
                        {
                            largeFiles.Add(new LargeFile
                            {
                                Path = file,
                                SizeBytes = info.Length,
                                LastModified = info.LastWriteTime
                            });

                            if (largeFiles.Count >= limit)
                                break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }, ct);

        return largeFiles.OrderByDescending(f => f.SizeBytes).ToList();
    }
}

public sealed class CleanupResult
{
    public string Operation { get; set; } = "";
    public bool Success { get; set; }
    public int FilesDeleted { get; set; }
    public long FreedBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan? Duration { get; set; }
}

public sealed class DiskUsageReport
{
    public List<DriveInfoModel> Drives { get; set; } = new();
    public List<FolderUsage> TopFolders { get; set; } = new();
}

public sealed class DriveInfoModel
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes { get; set; }
    public int UsagePercent { get; set; }
}

public sealed class FolderUsage
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed class LargeFile
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
}
