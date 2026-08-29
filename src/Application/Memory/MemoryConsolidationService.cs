using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Memory;

/// <summary>
/// Consolidation périodique de la mémoire : fusionne les entrées dupliquées,
/// applique le decay d'importance, nettoie les entrées expirées.
/// S'exécute toutes les 6 heures en tâche de fond via un IHostedService wrapper.
/// </summary>
public sealed class MemoryConsolidationService
{
    private readonly IMemoryStore _store;
    private readonly ILogger<MemoryConsolidationService> _logger;
    private Timer? _timer;

    public MemoryConsolidationService(IMemoryStore store, ILogger<MemoryConsolidationService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public void Start(CancellationToken cancellationToken)
    {
        _timer = new Timer(async _ => await RunAsync(cancellationToken), null,
            TimeSpan.FromMinutes(30), TimeSpan.FromHours(6));
        _logger.LogInformation("[MemoryConsolidation] Démarré (intervalle: 6h, première exécution: 30min)");
    }

    public void Stop() => _timer?.Dispose();

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("[MemoryConsolidation] Début de consolidation...");
            var all = await _store.GetAllAsync(cancellationToken);

            // 1. Suppression des entrées expirées
            var expired = await _store.DeleteExpiredAsync(cancellationToken);
            _logger.LogInformation("[MemoryConsolidation] {Expired} entrées expirées supprimées", expired);

            // 2. Decay d'importance : les entrées vieilles perdent de l'importance
            var decayed = 0;
            foreach (var entry in all)
            {
                if (entry.ExpiresAt != null) continue; // déjà géré par expiration
                var ageDays = (DateTime.UtcNow - entry.CreatedAt).TotalDays;
                if (ageDays > 30 && entry.Importance > 0.1f)
                {
                    var decayFactor = Math.Max(0.1, 1.0 - (ageDays - 30) * 0.01);
                    var newImportance = Math.Max(0.1f, entry.Importance * (float)decayFactor);
                    if (Math.Abs(newImportance - entry.Importance) > 0.01f)
                    {
                        entry.Importance = newImportance;
                        await _store.UpsertAsync(entry, cancellationToken);
                        decayed++;
                    }
                }
            }
            _logger.LogInformation("[MemoryConsolidation] {Decayed} entrées décrémentées en importance", decayed);

            // 3. Détection de doublons (même clé ou contenu quasi identique)
            var byKey = all.GroupBy(e => e.Key).Where(g => g.Count() > 1).ToList();
            var merged = 0;
            foreach (var group in byKey)
            {
                var entries = group.OrderByDescending(e => e.Importance).ToList();
                var keeper = entries[0];
                foreach (var dupe in entries.Skip(1))
                {
                    if (dupe.Content == keeper.Content)
                    {
                        await _store.DeleteAsync(dupe.Key, cancellationToken);
                        merged++;
                    }
                }
            }
            _logger.LogInformation("[MemoryConsolidation] {Merged} doublons fusionnés", merged);
            _logger.LogInformation("[MemoryConsolidation] Consolidation terminée.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MemoryConsolidation] Erreur lors de la consolidation");
        }
    }
}
