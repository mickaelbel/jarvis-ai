namespace JarvisAI.Application.Search;

public interface ISearchCache
{
    SearchResponse? Get(string key);
    void Set(string key, SearchResponse value, TimeSpan? ttl = null);
    void Invalidate();
    void InvalidateWhere(Func<string, bool> predicate);
    int Count { get; }
    int Hits { get; }
    int Misses { get; }
    IReadOnlyList<string> TopKeys(int count);
}

public sealed class SearchCacheOptions
{
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxEntries { get; set; } = 500;

    public IReadOnlyDictionary<SearchResultType, TimeSpan> PerTypeTtl { get; set; } =
        new Dictionary<SearchResultType, TimeSpan>
        {
            [SearchResultType.News] = TimeSpan.FromMinutes(5),
            [SearchResultType.Local] = TimeSpan.FromHours(24),
            [SearchResultType.OfficialSite] = TimeSpan.FromHours(6),
            [SearchResultType.Academic] = TimeSpan.FromHours(6),
            [SearchResultType.Repository] = TimeSpan.FromHours(3)
        };
}

public sealed class SearchCache : ISearchCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new();
    private readonly SearchCacheOptions _options;
    private int _hits;
    private int _misses;

    public SearchCache(SearchCacheOptions? options = null)
    {
        _options = options ?? new SearchCacheOptions();
    }

    public int Count
    {
        get { lock (_gate) { Purge(); return _entries.Count; } }
    }

    public int Hits { get { lock (_gate) return _hits; } }
    public int Misses { get { lock (_gate) return _misses; } }

    public SearchResponse? Get(string key)
    {
        lock (_gate)
        {
            Purge();
            if (_entries.TryGetValue(key, out var entry))
            {
                _hits++;
                entry.LastAccess = DateTime.UtcNow;
                entry.HitCount++;
                return entry.Response;
            }
            _misses++;
            return null;
        }
    }

    public void Set(string key, SearchResponse value, TimeSpan? ttl = null)
    {
        lock (_gate)
        {
            Purge();
            var resolvedTtl = ttl ?? _options.Ttl;
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.Response = value;
                existing.CreatedAt = DateTime.UtcNow;
                existing.Ttl = resolvedTtl;
                return;
            }
            _entries[key] = new CacheEntry(value, DateTime.UtcNow, resolvedTtl);
            while (_entries.Count > _options.MaxEntries)
            {
                var oldest = _entries.OrderBy(e => e.Value.LastAccess).First();
                _entries.Remove(oldest.Key);
            }
        }
    }

    public void Invalidate()
    {
        lock (_gate) _entries.Clear();
    }

    public void InvalidateWhere(Func<string, bool> predicate)
    {
        if (predicate is null)
            return;
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => predicate(k)).ToList())
                _entries.Remove(key);
        }
    }

    public IReadOnlyList<string> TopKeys(int count)
    {
        lock (_gate)
        {
            Purge();
            return _entries.OrderByDescending(e => e.Value.HitCount)
                .ThenByDescending(e => e.Value.LastAccess)
                .Take(Math.Max(0, count))
                .Select(e => e.Key)
                .ToList();
        }
    }

    private void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _entries.Where(e => now - e.Value.CreatedAt > e.Value.Ttl).Select(e => e.Key).ToList())
            _entries.Remove(key);
    }

    private sealed class CacheEntry
    {
        public SearchResponse Response { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccess { get; set; }
        public TimeSpan Ttl { get; set; }
        public int HitCount { get; set; }

        public CacheEntry(SearchResponse response, DateTime createdAt, TimeSpan ttl)
        {
            Response = response;
            CreatedAt = createdAt;
            LastAccess = createdAt;
            Ttl = ttl;
        }
    }
}
