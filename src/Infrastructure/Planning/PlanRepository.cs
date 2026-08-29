using System.Collections.Concurrent;
using System.Text.Json;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Planning;

/// <summary>
/// Persiste les plans sur disque (JSON) pour qu'ils survivent au redémarrage.
/// Les plans actifs restent en mémoire pour l'accès rapide, et sont sauvegardés
/// à chaque modification.
/// </summary>
public sealed class PlanRepository : IPlanRepository
{
    private readonly ConcurrentDictionary<Guid, Plan> _plans = new();
    private readonly ILogger<PlanRepository> _logger;
    private static readonly string PlansDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "plans");

    public PlanRepository(ILogger<PlanRepository> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(PlansDir);
        LoadAll();
    }

    public Task SaveAsync(Plan plan, CancellationToken cancellationToken = default)
    {
        _plans[plan.Id] = plan;
        Persist(plan);
        _logger.LogDebug("[PlanRepository] Plan sauvegardé {PlanId}: {Goal} ({StepCount} étapes)", plan.Id, plan.Goal, plan.Steps.Count);
        return Task.CompletedTask;
    }

    public Task<Plan?> GetByIdAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        _plans.TryGetValue(planId, out var plan);
        return Task.FromResult(plan);
    }

    public Task<IReadOnlyList<Plan>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var plans = _plans.Values.OrderByDescending(p => p.CreatedAt).ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<Plan>>(plans);
    }

    public Task<bool> DeleteAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        var removed = _plans.TryRemove(planId, out _);
        var path = GetPath(planId);
        if (File.Exists(path)) File.Delete(path);
        _logger.LogDebug("[PlanRepository] Plan supprimé {PlanId}: {Success}", planId, removed);
        return Task.FromResult(removed);
    }

    /// <summary>
    /// Charge un plan par son ID et le restaure en mémoire.
    /// Utile pour reprendre un plan interrompu.
    /// </summary>
    public Plan? LoadFromDisk(Guid planId)
    {
        var path = GetPath(planId);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var plan = JsonSerializer.Deserialize<Plan>(json);
            if (plan is not null) _plans[plan.Id] = plan;
            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PlanRepository] Erreur chargement plan {PlanId}", planId);
            return null;
        }
    }

    private void Persist(Plan plan)
    {
        try
        {
            var path = GetPath(plan.Id);
            var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PlanRepository] Erreur persistance plan {PlanId}", plan.Id);
        }
    }

    private void LoadAll()
    {
        try
        {
            foreach (var file in Directory.GetFiles(PlansDir, "*.json"))
            {
                var json = File.ReadAllText(file);
                var plan = JsonSerializer.Deserialize<Plan>(json);
                if (plan is not null) _plans[plan.Id] = plan;
            }
            _logger.LogInformation("[PlanRepository] {Count} plans chargés depuis le disque", _plans.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PlanRepository] Erreur chargement plans depuis le disque");
        }
    }

    private static string GetPath(Guid planId) => Path.Combine(PlansDir, $"{planId}.json");
}
