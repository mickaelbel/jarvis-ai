namespace JarvisAI.Application.Memory;

public interface IMemoryService
{
    Task<MemoryEntry> SaveAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    Task<MemoryEntry> SaveMemoryAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, MemoryTier tier = MemoryTier.LongTerm, string? project = null, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    Task<MemoryEntry?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryEntry>> SearchSemanticAsync(string query, int limit = 10, MemoryTier? tier = null, string? category = null, string? project = null, CancellationToken cancellationToken = default);
    Task<MemoryContext> BuildContextAsync(string query, string? project = null, int limitPerScope = 6, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryEntry>> GetContextAsync(string category, int maxEntries = 20, CancellationToken cancellationToken = default);
}
