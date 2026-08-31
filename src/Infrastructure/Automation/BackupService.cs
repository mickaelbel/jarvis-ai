using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Automation;

public interface IBackupService
{
    Task<BackupResult> BackupFolderAsync(string sourcePath, string? destinationPath = null, CancellationToken ct = default);
    Task<List<BackupInfo>> GetBackupsAsync();
    Task<BackupResult> RestoreBackupAsync(string backupId, string? restorePath = null, CancellationToken ct = default);
    Task<List<DuplicateInfo>> FindDuplicatesAsync(string path, CancellationToken ct = default);
    Task<int> RemoveDuplicatesAsync(IEnumerable<DuplicateInfo> duplicates, CancellationToken ct = default);
    Task<BackupSchedule> GetScheduleAsync();
    Task SaveScheduleAsync(BackupSchedule schedule);
}

public sealed class BackupService : IBackupService
{
    private readonly ILogger<BackupService> _logger;
    private readonly string _backupRoot;
    private readonly string _configPath;

    public BackupService(ILogger<BackupService> logger)
    {
        _logger = logger;
        _backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "Backups");
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "backup_schedule.json");
        Directory.CreateDirectory(_backupRoot);
    }

    public async Task<BackupResult> BackupFolderAsync(string sourcePath, string? destinationPath = null, CancellationToken ct = default)
    {
        var result = new BackupResult
        {
            SourcePath = sourcePath,
            StartedAt = DateTime.UtcNow
        };

        if (!Directory.Exists(sourcePath))
        {
            result.Success = false;
            result.ErrorMessage = $"Source not found: {sourcePath}";
            return result;
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var folderName = Path.GetFileName(sourcePath);
        var destDir = destinationPath ?? Path.Combine(_backupRoot, $"{folderName}_{timestamp}");
        Directory.CreateDirectory(destDir);

        try
        {
            var files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
            result.TotalFiles = files.Length;

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var relativePath = Path.GetRelativePath(sourcePath, file);
                    var destFile = Path.Combine(destDir, relativePath);
                    var destDirPart = Path.GetDirectoryName(destFile);

                    if (destDirPart is not null && !Directory.Exists(destDirPart))
                        Directory.CreateDirectory(destDirPart);

                    File.Copy(file, destFile, true);
                    result.CopiedFiles++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Backup] Failed to copy: {File}", file);
                    result.Errors.Add(file);
                }
            }

            // Save metadata
            var metadata = new BackupMetadata
            {
                Id = Path.GetFileName(destDir),
                SourcePath = sourcePath,
                DestinationPath = destDir,
                CreatedAt = DateTime.UtcNow,
                FileCount = result.CopiedFiles,
                TotalSizeBytes = GetDirectorySize(destDir)
            };
            await SaveMetadataAsync(metadata, ct);

            result.Success = true;
            result.DestinationPath = destDir;
            result.CompletedAt = DateTime.UtcNow;
            result.Duration = result.CompletedAt.Value - result.StartedAt;

            _logger.LogInformation("[Backup] Completed: {Files}/{Total} files to {Dest}",
                result.CopiedFiles, result.TotalFiles, destDir);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Backup] Failed");
        }

        return result;
    }

    public async Task<List<BackupInfo>> GetBackupsAsync()
    {
        var backups = new List<BackupInfo>();

        if (!Directory.Exists(_backupRoot))
            return backups;

        foreach (var dir in Directory.GetDirectories(_backupRoot))
        {
            try
            {
                var metadataFile = Path.Combine(dir, "backup_metadata.json");
                if (File.Exists(metadataFile))
                {
                    var json = await File.ReadAllTextAsync(metadataFile);
                    var metadata = JsonSerializer.Deserialize<BackupMetadata>(json);
                    if (metadata is not null)
                    {
                        backups.Add(new BackupInfo
                        {
                            Id = metadata.Id,
                            SourcePath = metadata.SourcePath,
                            CreatedAt = metadata.CreatedAt,
                            FileCount = metadata.FileCount,
                            TotalSizeBytes = metadata.TotalSizeBytes
                        });
                    }
                }
            }
            catch { }
        }

        return backups.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public async Task<BackupResult> RestoreBackupAsync(string backupId, string? restorePath = null, CancellationToken ct = default)
    {
        var backupDir = Path.Combine(_backupRoot, backupId);
        if (!Directory.Exists(backupDir))
        {
            return new BackupResult
            {
                Success = false,
                ErrorMessage = $"Backup not found: {backupId}"
            };
        }

        var metadataFile = Path.Combine(backupDir, "backup_metadata.json");
        if (!File.Exists(metadataFile))
        {
            return new BackupResult
            {
                Success = false,
                ErrorMessage = "Backup metadata not found"
            };
        }

        var json = await File.ReadAllTextAsync(metadataFile);
        var metadata = JsonSerializer.Deserialize<BackupMetadata>(json);
        var destPath = restorePath ?? metadata?.SourcePath ?? "";

        if (string.IsNullOrEmpty(destPath))
        {
            return new BackupResult
            {
                Success = false,
                ErrorMessage = "No restore path specified"
            };
        }

        return await BackupFolderAsync(backupDir, destPath, ct);
    }

    public async Task<List<DuplicateInfo>> FindDuplicatesAsync(string path, CancellationToken ct = default)
    {
        var duplicates = new List<DuplicateInfo>();
        var hashGroups = new Dictionary<string, List<string>>();

        if (!Directory.Exists(path))
            return duplicates;

        var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(file);
                var hash = Convert.ToBase64String(await sha.ComputeHashAsync(stream, ct));

                if (!hashGroups.ContainsKey(hash))
                    hashGroups[hash] = new List<string>();

                hashGroups[hash].Add(file);
            }
            catch { }
        }

        foreach (var group in hashGroups.Where(g => g.Value.Count > 1))
        {
            duplicates.Add(new DuplicateInfo
            {
                Hash = group.Key,
                Files = group.Value,
                TotalSize = group.Value.Sum(f => new FileInfo(f).Length)
            });
        }

        return duplicates.OrderByDescending(d => d.TotalSize).ToList();
    }

    public Task<int> RemoveDuplicatesAsync(IEnumerable<DuplicateInfo> duplicates, CancellationToken ct = default)
    {
        var removed = 0;

        foreach (var dup in duplicates)
        {
            // Keep the first file, delete the rest
            foreach (var file in dup.Files.Skip(1))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    File.Delete(file);
                    removed++;
                    _logger.LogInformation("[Backup] Removed duplicate: {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Backup] Failed to delete: {File}", file);
                }
            }
        }

        return Task.FromResult(removed);
    }

    public async Task<BackupSchedule> GetScheduleAsync()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = await File.ReadAllTextAsync(_configPath);
                return JsonSerializer.Deserialize<BackupSchedule>(json) ?? new BackupSchedule();
            }
        }
        catch { }

        return new BackupSchedule();
    }

    public async Task SaveScheduleAsync(BackupSchedule schedule)
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(schedule, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_configPath, json);
        }
        catch { }
    }

    private static long GetDirectorySize(string path)
    {
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    private async Task SaveMetadataAsync(BackupMetadata metadata, CancellationToken ct)
    {
        var metadataFile = Path.Combine(metadata.DestinationPath, "backup_metadata.json");
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataFile, json, ct);
    }
}

public sealed class BackupResult
{
    public bool Success { get; set; }
    public string SourcePath { get; set; } = "";
    public string? DestinationPath { get; set; }
    public int TotalFiles { get; set; }
    public int CopiedFiles { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
}

public sealed class BackupInfo
{
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
}

public sealed class DuplicateInfo
{
    public string Hash { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public long TotalSize { get; set; }
}

public sealed class BackupSchedule
{
    public bool Enabled { get; set; }
    public int Hour { get; set; } = 2;
    public int Minute { get; set; } = 0;
    public string Days { get; set; } = "Mon,Wed,Fri";
    public List<string> SourcePaths { get; set; } = new();
    public int RetentionDays { get; set; } = 30;
}

internal class BackupMetadata
{
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
}
