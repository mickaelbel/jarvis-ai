using JarvisAI.Application.AI;
using JarvisAI.Application.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Maintenance périodique : purge des mémoires expirées, éviction des mémoires
/// court terme à faible importance au-delà d'un plafond, et nettoyage du cache
/// de réponses pour borner la mémoire vive.
/// </summary>
public sealed class MemoryMaintenanceService : BackgroundService
{
    private readonly IMemoryService _memory;
    private readonly IMemoryStore _store;
    private readonly IResponseCache _cache;
    private readonly ILogger<MemoryMaintenanceService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private const int MaxShortTermMemories = 100;
    private const float MinShortTermImportance = 0.25f;

    public MemoryMaintenanceService(
        IMemoryService memory,
        IMemoryStore store,
        IResponseCache cache,
        ILogger<MemoryMaintenanceService> logger)
    {
        _memory = memory;
        _store = store;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemoryMaintenance] Échec de la maintenance mémoire");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        var expired = await _memory.CleanupExpiredAsync(cancellationToken);
        var cachePruned = _cache.PruneExpired();
        if (expired > 0 || cachePruned > 0)
            _logger.LogInformation("[MemoryMaintenance] Purge : {Expired} mémoires expirées, {Cache} entrées de cache", expired, cachePruned);

        var shortTerm = await _store.QueryAsync(
            null, null, null, null, null, false, int.MaxValue, false, MemoryTier.ShortTerm, null, cancellationToken);

        if (shortTerm.Count > MaxShortTermMemories)
        {
            var toEvict = shortTerm
                .OrderByDescending(e => e.Importance)
                .Skip(MaxShortTermMemories)
                .Where(e => e.Importance < MinShortTermImportance)
                .ToList();

            foreach (var entry in toEvict)
            {
                await _store.DeleteAsync(entry.Key, cancellationToken);
            }

            if (toEvict.Count > 0)
                _logger.LogInformation("[MemoryMaintenance] Éviction de {Count} mémoires court terme à faible importance", toEvict.Count);
        }
    }
}
