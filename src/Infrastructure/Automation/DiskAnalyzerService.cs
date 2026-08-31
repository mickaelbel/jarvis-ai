using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IDiskAnalyzerService
{
    Task<DiskReport> AnalyzeDiskAsync(string path, CancellationToken ct = default);
    Task<List<LargeFolder>> GetLargestFoldersAsync(string path, int limit = 20, CancellationToken ct = default);
    Task<List<LargeFile>> GetLargestFilesAsync(string path, int limit = 20, long minSizeMb = 10, CancellationToken ct = default);
    Task<DiskReport> GetFullReportAsync(CancellationToken ct = default);
}

public sealed class DiskAnalyzerService : IDiskAnalyzerService
{
    private readonly ILogger<DiskAnalyzerService> _logger;

    public DiskAnalyzerService(ILogger<DiskAnalyzerService> logger)
    {
        _logger = logger;
    }

    public async Task<DiskReport> AnalyzeDiskAsync(string path, CancellationToken ct = default)
    {
        var report = new DiskReport { Path = path };

        await Task.Run(() =>
        {
            try
            {
                var info = new DriveInfo(Path.GetPathRoot(path) ?? path);
                report.TotalBytes = info.TotalSize;
                report.FreeBytes = info.AvailableFreeSpace;
                report.UsedBytes = info.TotalSize - info.AvailableFreeSpace;
                report.UsagePercent = (int)(report.UsedBytes * 100.0 / report.TotalBytes);
            }
            catch { }

            // Analyze folder sizes
            var topDirs = new[] { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "AppData" };
            foreach (var dir in topDirs)
            {
                var dirPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dir);
                if (Directory.Exists(dirPath))
                {
                    try
                    {
                        var size = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories)
                            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });

                        report.FolderSizes.Add(new FolderSize { Path = dirPath, SizeBytes = size });
                    }
                    catch { }
                }
            }
        }, ct);

        report.FolderSizes = report.FolderSizes.OrderByDescending(f => f.SizeBytes).ToList();
        return report;
    }

    public async Task<List<LargeFolder>> GetLargestFoldersAsync(string path, int limit = 20, CancellationToken ct = default)
    {
        var folders = new List<LargeFolder>();

        await Task.Run(() =>
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                if (ct.IsCancellationRequested || folders.Count >= limit) break;

                try
                {
                    var size = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });

                    folders.Add(new LargeFolder { Path = dir, SizeBytes = size, FileCount = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length });
                }
                catch { }
            }
        }, ct);

        return folders.OrderByDescending(f => f.SizeBytes).Take(limit).ToList();
    }

    public async Task<List<LargeFile>> GetLargestFilesAsync(string path, int limit = 20, long minSizeMb = 10, CancellationToken ct = default)
    {
        var files = new List<LargeFile>();
        var minSizeBytes = minSizeMb * 1024 * 1024;

        await Task.Run(() =>
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested || files.Count >= limit * 2) break;

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length >= minSizeBytes)
                    {
                        files.Add(new LargeFile { Path = file, SizeBytes = info.Length, LastModified = info.LastWriteTime });
                    }
                }
                catch { }
            }
        }, ct);

        return files.OrderByDescending(f => f.SizeBytes).Take(limit).ToList();
    }

    public async Task<DiskReport> GetFullReportAsync(CancellationToken ct = default)
    {
        var report = await AnalyzeDiskAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ct);
        report.LargestFolders = await GetLargestFoldersAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 10, ct);
        report.LargestFiles = await GetLargestFilesAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 10, 10, ct);
        return report;
    }
}

public sealed class DiskReport
{
    public string Path { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes { get; set; }
    public int UsagePercent { get; set; }
    public List<FolderSize> FolderSizes { get; set; } = new();
    public List<LargeFolder> LargestFolders { get; set; } = new();
    public List<LargeFile> LargestFiles { get; set; } = new();
}

public sealed class FolderSize
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed class LargeFolder
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
}

public sealed class LargeFile
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
}
