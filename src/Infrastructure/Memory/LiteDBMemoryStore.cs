using JarvisAI.Application.Memory;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Memory;

public sealed class LiteDBMemoryStore : IMemoryStore, IDisposable
{
    private readonly ILiteDatabase _db;
    private readonly ILiteCollection<MemoryEntry> _collection;
    private readonly ILogger<LiteDBMemoryStore> _logger;
    private bool _disposed;

    public LiteDBMemoryStore(ILogger<LiteDBMemoryStore> logger, string databasePath = ":memory:")
    {
        _logger = logger;
        _db = new LiteDatabase(databasePath);
        _collection = _db.GetCollection<MemoryEntry>("memories");
        _collection.EnsureIndex(x => x.Key, unique: true);
        _collection.EnsureIndex(x => x.Category);
        _collection.EnsureIndex(x => x.Type);
        _collection.EnsureIndex(x => x.Tier);
        _collection.EnsureIndex(x => x.ProjectName);
        _collection.EnsureIndex(x => x.ExpiresAt);
        _collection.EnsureIndex(x => x.CreatedAt);
        _logger.LogInformation("[LiteDBMemoryStore] Initialized with path: {Path}", databasePath);
    }

    public LiteDBMemoryStore(ILogger<LiteDBMemoryStore> logger, ILiteDatabase database)
    {
        _logger = logger;
        _db = database;
        _collection = _db.GetCollection<MemoryEntry>("memories");
        _collection.EnsureIndex(x => x.Key, unique: true);
        _collection.EnsureIndex(x => x.Category);
        _collection.EnsureIndex(x => x.Type);
        _collection.EnsureIndex(x => x.Tier);
        _collection.EnsureIndex(x => x.ProjectName);
        _collection.EnsureIndex(x => x.ExpiresAt);
        _collection.EnsureIndex(x => x.CreatedAt);
    }

    public Task<MemoryEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var entry = _collection.FindOne(x => x.Key == key);
        return Task.FromResult<MemoryEntry?>(entry);
    }

    public Task<IReadOnlyList<MemoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MemoryEntry> entries = _collection.FindAll().ToList();
        return Task.FromResult(entries);
    }

    public Task UpsertAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        _collection.Upsert(entry);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = _collection.DeleteMany(x => x.Key == key);
        return Task.FromResult(result > 0);
    }

    public Task<IReadOnlyList<MemoryEntry>> QueryAsync(
        string? key, string? category, MemoryType? type, string? textSearch,
        float? minImportance, bool includeExpired, int? limit, bool orderByNewest,
        MemoryTier? tier = null, string? project = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<MemoryEntry> results = _collection.FindAll();

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
        {
            results = results.Where(x =>
                x.Content.Contains(textSearch, StringComparison.OrdinalIgnoreCase) ||
                x.Key.Contains(textSearch, StringComparison.OrdinalIgnoreCase));
        }

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
        var count = _collection.DeleteMany(x => x.ExpiresAt != null && x.ExpiresAt < DateTime.UtcNow);
        return Task.FromResult(count);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _db.Dispose();
            _disposed = true;
        }
    }
}
