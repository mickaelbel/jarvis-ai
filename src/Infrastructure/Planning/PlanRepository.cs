using System.Collections.Concurrent;
using JarvisAI.Application.Planning;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Planning;

public sealed class PlanRepository : IPlanRepository
{
    private readonly ConcurrentDictionary<Guid, Plan> _plans = new();
    private readonly ILogger<PlanRepository> _logger;

    public PlanRepository(ILogger<PlanRepository> logger)
    {
        _logger = logger;
    }

    public Task SaveAsync(Plan plan, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[PlanRepository] Saving plan {PlanId}: {Goal} ({StepCount} steps)", plan.Id, plan.Goal, plan.Steps.Count);
        _plans[plan.Id] = plan;
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
        _logger.LogDebug("[PlanRepository] Deleted plan {PlanId}: {Success}", planId, removed);
        return Task.FromResult(removed);
    }
}
