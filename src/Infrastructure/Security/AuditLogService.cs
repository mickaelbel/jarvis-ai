using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Security;

public sealed class AuditLogService : IAuditLogService
{
    private readonly ILogger<AuditLogService> _logger;
    private readonly ConcurrentBag<AuditEntry> _entries = new();
    private readonly string _logDir;
    private static readonly int MaxInMemory = 1000;

    public AuditLogService(ILogger<AuditLogService> logger)
    {
        _logger = logger;
        _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI", "audit");
        Directory.CreateDirectory(_logDir);
        LoadFromDisk();
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        _entries.Add(entry);

        // Log rotation: si > 1000 entrées, sauvegarder et purger
        if (_entries.Count > MaxInMemory)
            await FlushToDiskAsync();

        _logger.LogInformation("[Audit] {Tool}.{Action} {Status} ({Duration}ms) {Error}",
            entry.ToolName, entry.Action,
            entry.Success ? "OK" : "FAIL",
            entry.DurationMs,
            entry.Error ?? "");
    }

    public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int count = 50, CancellationToken ct = default)
    {
        var entries = _entries
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<AuditEntry>>(entries);
    }

    public Task<IReadOnlyList<AuditEntry>> GetByToolAsync(string toolName, int count = 50, CancellationToken ct = default)
    {
        var entries = _entries
            .Where(e => e.ToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<AuditEntry>>(entries);
    }

    public Task<IReadOnlyList<AuditEntry>> GetErrorsAsync(int count = 50, CancellationToken ct = default)
    {
        var entries = _entries
            .Where(e => !e.Success)
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<AuditEntry>>(entries);
    }

    public Task<AuditStats> GetStatsAsync(CancellationToken ct = default)
    {
        var all = _entries.ToList();
        var byTool = all.GroupBy(e => e.ToolName)
            .ToDictionary(g => g.Key, g => g.Count());
        var errors = all.Where(e => !e.Success).GroupBy(e => e.Error ?? "Unknown")
            .ToDictionary(g => g.Key, g => g.Count());
        var avgDur = all.Count > 0 ? all.Average(e => e.DurationMs) : 0;

        return Task.FromResult(new AuditStats(
            all.Count,
            all.Count(e => e.Success),
            all.Count(e => !e.Success),
            byTool,
            errors,
            avgDur));
    }

    private async Task FlushToDiskAsync()
    {
        try
        {
            var fileName = Path.Combine(_logDir, $"audit_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            var entries = _entries.OrderBy(e => e.Timestamp).ToList();
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(fileName, json);
            _logger.LogInformation("[Audit] Flushed {Count} entries to {File}", entries.Count, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Audit] Failed to flush to disk");
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            var files = Directory.GetFiles(_logDir, "audit_*.json")
                .OrderByDescending(f => f)
                .Take(5) // Charger les 5 derniers fichiers
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var entries = JsonSerializer.Deserialize<List<AuditEntry>>(json);
                    if (entries is not null)
                        foreach (var entry in entries)
                            _entries.Add(entry);
                }
                catch { }
            }
        }
        catch { }
    }
}
