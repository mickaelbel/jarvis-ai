using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace JarvisAI.Application.AI;

public sealed class CachedResponse
{
    public required string Key { get; init; }
    public required string Content { get; init; }
    public required string Model { get; init; }
    public DateTime CreatedUtc { get; init; }
}

public interface IResponseCache
{
    CachedResponse? TryGet(string userMessage, string? model);
    void Set(string userMessage, string model, string content, int maxBytes = 4000);
    void Clear();
    int PruneExpired();
    IReadOnlyList<CachedResponse> GetAll();
}

public sealed class InMemoryResponseCache : IResponseCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> NonCacheableWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ouvre", "ouvrir", "lance", "lancer", "ferme", "stop", "arrete", "execute",
        "exécute", "cree", "crée", "installe", "supprime", "telecharge", "télécharge",
        "navigateur", "chrome", "terminal", "commande", "run", "open", "start"
    };

    private readonly ILogger<InMemoryResponseCache> _logger;
    private readonly ConcurrentDictionary<string, CachedResponse> _cache = new(StringComparer.Ordinal);

    public InMemoryResponseCache(ILogger<InMemoryResponseCache> logger) => _logger = logger;

    public CachedResponse? TryGet(string userMessage, string? model)
    {
        if (!IsCacheable(userMessage)) return null;
        var key = BuildKey(userMessage, model);
        if (!_cache.TryGetValue(key, out var entry)) return null;
        if (DateTime.UtcNow - entry.CreatedUtc > DefaultTtl)
        {
            _cache.TryRemove(key, out _);
            return null;
        }
        return entry;
    }

    public void Set(string userMessage, string model, string content, int maxBytes = 4000)
    {
        if (!IsCacheable(userMessage)) return;
        if (string.IsNullOrWhiteSpace(content)) return;
        if (content.Length > maxBytes) return;
        var key = BuildKey(userMessage, model);
        _cache[key] = new CachedResponse { Key = key, Content = content, Model = model, CreatedUtc = DateTime.UtcNow };
    }

    public void Clear() => _cache.Clear();

    public int PruneExpired()
    {
        var now = DateTime.UtcNow;
        var removed = 0;
        foreach (var kv in _cache)
        {
            if (now - kv.Value.CreatedUtc > DefaultTtl && _cache.TryRemove(kv.Key, out _))
                removed++;
        }
        return removed;
    }

    public IReadOnlyList<CachedResponse> GetAll()
    {
        PruneExpired();
        return _cache.Values.OrderByDescending(e => e.CreatedUtc).ToArray();
    }

    private static bool IsCacheable(string userMessage)
    {
        var text = (userMessage ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text) || text.Length < 8) return false;
        foreach (var word in NonCacheableWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string BuildKey(string userMessage, string? model)
    {
        var normalized = new string(userMessage.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '\'').ToArray());
        var raw = model ?? "default";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized + "|" + raw));
        return Convert.ToHexString(hash);
    }
}
