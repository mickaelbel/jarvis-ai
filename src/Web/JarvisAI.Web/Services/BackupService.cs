using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IBackupService
{
    Task<string> CreateBackupAsync(string? description = null, CancellationToken ct = default);
    Task<IReadOnlyList<BackupInfo>> GetBackupsAsync(CancellationToken ct = default);
    Task<bool> RestoreBackupAsync(string backupId, CancellationToken ct = default);
    Task DeleteBackupAsync(string backupId, CancellationToken ct = default);
    Task ScheduleAutoBackupAsync(AutoBackupSchedule schedule, CancellationToken ct = default);
    AutoBackupSchedule? GetSchedule();
}

public sealed class BackupService : IBackupService
{
    private readonly ILogger<BackupService> _logger;
    private readonly string _backupsDir;
    private readonly string _schedulePath;
    private readonly List<BackupInfo> _backups = new();
    private AutoBackupSchedule? _schedule;

    public BackupService(ILogger<BackupService> logger)
    {
        _logger = logger;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _backupsDir = Path.Combine(appData, "JarvisAI", "backups");
        _schedulePath = Path.Combine(appData, "JarvisAI", "backup_schedule.json");
        Directory.CreateDirectory(_backupsDir);
        Load();
    }

    public async Task<string> CreateBackupAsync(string? description = null, CancellationToken ct = default)
    {
        var backupId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = Path.Combine(_backupsDir, backupId);
        Directory.CreateDirectory(backupPath);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var jarvisDir = Path.Combine(appData, "JarvisAI");

        var filesToBackup = new[]
        {
            "settings.json", "memory.db", "personas.json",
            "hotkeys.json", "webhooks.json", "notes.json"
        };

        foreach (var file in filesToBackup)
        {
            var src = Path.Combine(jarvisDir, file);
            if (File.Exists(src))
            {
                var dst = Path.Combine(backupPath, file);
                File.Copy(src, dst, overwrite: true);
            }
        }

        var info = new BackupInfo
        {
            Id = backupId,
            Description = description ?? "Auto-backup",
            CreatedAt = DateTime.UtcNow,
            Path = backupPath,
            FileCount = Directory.GetFiles(backupPath).Length
        };

        _backups.Add(info);
        Save();

        _logger.LogInformation("[Backup] Created: {Id} ({Files} files)", backupId, info.FileCount);
        return backupId;
    }

    public async Task<IReadOnlyList<BackupInfo>> GetBackupsAsync(CancellationToken ct = default)
    {
        return await Task.FromResult(_backups.OrderByDescending(b => b.CreatedAt).ToList());
    }

    public async Task<bool> RestoreBackupAsync(string backupId, CancellationToken ct = default)
    {
        var backup = _backups.FirstOrDefault(b => b.Id == backupId);
        if (backup is null || !Directory.Exists(backup.Path)) return false;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var jarvisDir = Path.Combine(appData, "JarvisAI");

        foreach (var file in Directory.GetFiles(backup.Path))
        {
            var dst = Path.Combine(jarvisDir, Path.GetFileName(file));
            File.Copy(file, dst, overwrite: true);
        }

        _logger.LogInformation("[Backup] Restored: {Id}", backupId);
        return true;
    }

    public async Task DeleteBackupAsync(string backupId, CancellationToken ct = default)
    {
        var backup = _backups.FirstOrDefault(b => b.Id == backupId);
        if (backup is not null && Directory.Exists(backup.Path))
            Directory.Delete(backup.Path, recursive: true);

        _backups.RemoveAll(b => b.Id == backupId);
        Save();
        await Task.CompletedTask;
    }

    public async Task ScheduleAutoBackupAsync(AutoBackupSchedule schedule, CancellationToken ct = default)
    {
        _schedule = schedule;
        SaveSchedule();
        _logger.LogInformation("[Backup] Schedule set: {Frequency}", schedule.Frequency);
        await Task.CompletedTask;
    }

    public AutoBackupSchedule? GetSchedule() => _schedule;

    private void Load()
    {
        try
        {
            if (File.Exists(_schedulePath))
            {
                var json = File.ReadAllText(_schedulePath);
                _schedule = JsonSerializer.Deserialize<AutoBackupSchedule>(json);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_backups, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_backupsDir, "backups.json"), json);
        }
        catch { }
    }

    private void SaveSchedule()
    {
        try
        {
            var json = JsonSerializer.Serialize(_schedule, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_schedulePath, json);
        }
        catch { }
    }
}

public sealed class BackupInfo
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Path { get; set; } = "";
    public int FileCount { get; set; }
}

public sealed class AutoBackupSchedule
{
    public string Frequency { get; set; } = "daily";
    public int Hour { get; set; } = 2;
    public bool IsEnabled { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
}
