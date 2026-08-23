namespace JarvisAI.Application.Agents;

public interface IRunDataStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(RunRecord record, CancellationToken cancellationToken = default);
    Task<RunRecord?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RunRecord>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid runId, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}