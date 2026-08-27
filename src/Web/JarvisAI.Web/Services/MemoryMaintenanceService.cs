using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Maintenance périodique de la mémoire :
///  - purge des mémoires expirées,
///  - éviction des mémoires court terme à faible importance au-delà d'un plafond,
///  - consolide la mémoire long terme : les souvenirs non durables (ni épinglés par
///    l'utilisateur) sont relégués en court terme avec une expiration adaptée, afin
///    d'éviter que des « trucs inutiles » s'accumulent durablement,
///  - nettoyage du cache de réponses.
/// </summary>
public sealed class MemoryMaintenanceService : BackgroundService
{
    private readonly IMemoryService _memory;
    private readonly IMemoryStore _store;
    private readonly IAutomaticMemoryService _autoMemory;
    private readonly IResponseCache _cache;
    private readonly ILogger<MemoryMaintenanceService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private const int MaxShortTermMemories = 100;
    private const float MinShortTermImportance = 0.25f;

    public MemoryMaintenanceService(
        IMemoryService memory,
        IMemoryStore store,
        IAutomaticMemoryService autoMemory,
        IResponseCache cache,
        ILogger<MemoryMaintenanceService> logger)
    {
        _memory = memory;
        _store = store;
        _autoMemory = autoMemory;
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

        var demoted = await ConsolidateLongTermAsync(cancellationToken);
        if (demoted > 0)
            _logger.LogInformation("[MemoryMaintenance] {Count} souvenirs long terme non durables relégués en court terme", demoted);

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

    /// <summary>
    /// Analyse les souvenirs long terme existants avec le même filtre strict que les
    /// nouveaux souvenirs. Ceux qui ne sont ni durables ni épinglés manuellement sont
    /// rétrogradés (contenu et historique conservés) en court terme avec une expiration
    /// adaptée, plutôt que d'être supprimés.
    /// </summary>
    private async Task<int> ConsolidateLongTermAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MemoryEntry> longTerm;
        try
        {
            longTerm = await _store.QueryAsync(
                null, null, null, null, null, false, int.MaxValue, false, MemoryTier.LongTerm, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MemoryMaintenance] Consolidation long terme impossible (store indisponible ?)");
            return 0;
        }

        var demoted = 0;
        foreach (var entry in longTerm)
        {
            if (IsPinned(entry))
                continue;

            if (_autoMemory.IsDurable(entry.Content, entry.Type, entry.Category))
                continue;

            var importance = Math.Max(4, _autoMemory.ComputeImportance(entry.Content));
            var ttl = _autoMemory.DecideExpiration(importance, MemoryTier.ShortTerm);
            var expiry = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : (DateTime?)null;

            var metadata = new Dictionary<string, string>(entry.Metadata)
            {
                ["durable"] = "false",
                ["demoted"] = "true"
            };

            try
            {
                await _memory.SaveMemoryAsync(
                    entry.Key, entry.Content, entry.Type, entry.Category,
                    importance: entry.Importance,
                    tier: MemoryTier.ShortTerm,
                    project: entry.ProjectName,
                    ttl: ttl,
                    metadata: metadata,
                    cancellationToken: cancellationToken);
                demoted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MemoryMaintenance] Échec de relégation de la mémoire {Key}", entry.Key);
            }
        }

        return demoted;
    }

    private static bool IsPinned(MemoryEntry entry)
        => entry.Metadata is not null
           && entry.Metadata.TryGetValue(AutomaticMemoryService.PinnedMetadataKey, out var pinned)
           && string.Equals(pinned, "true", StringComparison.OrdinalIgnoreCase);
}
