using JarvisAI.Application.Memory;

namespace JarvisAI.Tests;

internal sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly Dictionary<string, MemoryEntry> _store = new();

    public Task<MemoryEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(key, out var entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<MemoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(_store.Values.ToList());
    }

    public Task UpsertAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        _store[entry.Key] = entry;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_store.Remove(key));
    }

    public Task<IReadOnlyList<MemoryEntry>> QueryAsync(
        string? key, string? category, MemoryType? type, string? textSearch,
        float? minImportance, bool includeExpired, int? limit, bool orderByNewest,
        MemoryTier? tier = null, string? project = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<MemoryEntry> results = _store.Values;

        if (!string.IsNullOrEmpty(key))
            results = results.Where(x => x.Key == key);

        if (!string.IsNullOrEmpty(category))
            results = results.Where(x => x.Category == category);

        if (type.HasValue)
            results = results.Where(x => x.Type == type.Value);

        if (tier.HasValue)
            results = results.Where(x => x.Tier == tier.Value);

        if (!string.IsNullOrEmpty(project))
            results = results.Where(x => string.Equals(x.ProjectName, project, StringComparison.OrdinalIgnoreCase));

        if (minImportance.HasValue)
            results = results.Where(x => x.Importance >= minImportance.Value);

        if (!includeExpired)
            results = results.Where(x => x.ExpiresAt == null || x.ExpiresAt > DateTime.UtcNow);

        if (!string.IsNullOrEmpty(textSearch))
            results = results.Where(x =>
                x.Content.Contains(textSearch, StringComparison.OrdinalIgnoreCase) ||
                x.Key.Contains(textSearch, StringComparison.OrdinalIgnoreCase));

        if (orderByNewest)
            results = results.OrderByDescending(x => x.CreatedAt);
        else
            results = results.OrderBy(x => x.CreatedAt);

        IReadOnlyList<MemoryEntry> list;
        if (limit.HasValue)
            list = results.Take(limit.Value).ToList();
        else
            list = results.ToList();

        return Task.FromResult(list);
    }

    public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        var expired = _store.Where(x => x.Value.ExpiresAt.HasValue && x.Value.ExpiresAt < DateTime.UtcNow).ToList();
        foreach (var kvp in expired)
            _store.Remove(kvp.Key);
        return Task.FromResult(expired.Count);
    }
}
