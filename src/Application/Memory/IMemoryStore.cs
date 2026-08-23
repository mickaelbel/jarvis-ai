namespace JarvisAI.Application.Memory;

public interface IMemoryStore
{
    Task<MemoryEntry?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryEntry>> GetAllAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(MemoryEntry entry, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryEntry>> QueryAsync(string? key, string? category, MemoryType? type, string? textSearch, float? minImportance, bool includeExpired, int? limit, bool orderByNewest, MemoryTier? tier = null, string? project = null, CancellationToken cancellationToken = default);
    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default);
}
