namespace JarvisAI.Application.Planning;

public interface IPlanner
{
    Task<Plan> CreatePlanAsync(string goal, CancellationToken cancellationToken = default);
    Task<Plan> CreatePlanWithContextAsync(string goal, IReadOnlyList<string> context, CancellationToken cancellationToken = default);
    Task<Plan> CreatePlanWithInstructionsAsync(string goal, string instructions, IReadOnlyList<string> context, CancellationToken cancellationToken = default);
}

public interface IPlanRepository
{
    Task SaveAsync(Plan plan, CancellationToken cancellationToken = default);
    Task<Plan?> GetByIdAsync(Guid planId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Plan>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid planId, CancellationToken cancellationToken = default);
}
