using System.Diagnostics;
using JarvisAI.Application.Search;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Search;

public sealed class WebSearchService : IWebSearchService
{
    private readonly IReadOnlyList<IWebSearchProvider> _providers;
    private readonly ILinkVerifier _linkVerifier;
    private readonly ISearchCache _cache;
    private readonly OfficialSiteDetector _officialDetector;
    private readonly FakeSiteDetector _fakeSiteDetector;
    private readonly SearchResultDeduper _deduper = new();
    private readonly NearDuplicateDetector _nearDuplicateDetector = new();
    private readonly TopicResultGrouper _grouper = new();
    private readonly ConsensusAnalyzer _consensus = new();
    private readonly ResultRanker _ranker = new();
    private readonly EntityConsolidator _entityConsolidator = new();
    private readonly ILogger<WebSearchService> _logger;
    private readonly SearchCacheOptions _options;
    private readonly object _healthGate = new();
    private readonly Dictionary<string, ProviderHealth> _health = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _hotGate = new();
    private readonly Dictionary<string, (int Count, SearchRequest Request)> _hotQueries = new(StringComparer.Ordinal);

    public WebSearchService(
        IEnumerable<IWebSearchProvider> providers,
        ILinkVerifier linkVerifier,
        ISearchCache cache,
        OfficialSiteDetector officialDetector,
        FakeSiteDetector fakeSiteDetector,
        ILogger<WebSearchService> logger,
        SearchCacheOptions? options = null)
    {
        _providers = providers.ToList();
        _linkVerifier = linkVerifier;
        _cache = cache;
        _officialDetector = officialDetector;
        _fakeSiteDetector = fakeSiteDetector;
        _logger = logger;
        _options = options ?? new SearchCacheOptions();
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(request);
        if (_cache.Get(key) is { } cached)
        {
            RecordHit(key, request);
            return Clone(cached, fromCache: true);
        }

        var providers = SelectProviders(request);
        if (providers.Count == 0)
        {
            RecordHit(key, request);
            return new SearchResponse { Query = request.Query, ProvidersUsed = Array.Empty<string>(), CompletedAt = DateTimeOffset.UtcNow };
        }

        var stats = new List<ProviderCallResult>();
        var allResults = new List<SearchResult>();

        // Parallélisation des providers (réduit latence = max(timeouts) au lieu de sum(timeouts))
        var providerResultsBag = new System.Collections.Concurrent.ConcurrentBag<(string Provider, IReadOnlyList<SearchResult> Results, long ElapsedMs, bool Success)>();
        var providerTasks = providers.Select(async provider =>
        {
            var sw = Stopwatch.StartNew();
            IReadOnlyList<SearchResult> results;
            try
            {
                results = await provider.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(ex, "[WebSearch] Provider {Provider} failed for {Query}", provider.Name, request.Query);
                RecordHealth(provider.Name, success: false, sw.ElapsedMilliseconds, 0);
                providerResultsBag.Add((provider.Name, Array.Empty<SearchResult>(), sw.ElapsedMilliseconds, false));
                return;
            }
            sw.Stop();
            foreach (var r in results)
            {
                if (string.IsNullOrWhiteSpace(r.Provider))
                    r.Provider = provider.Name;
            }
            RecordHealth(provider.Name, success: true, sw.ElapsedMilliseconds, results.Count);
            providerResultsBag.Add((provider.Name, results, sw.ElapsedMilliseconds, true));
        }).ToList();

        await Task.WhenAll(providerTasks).ConfigureAwait(false);

        foreach (var entry in providerResultsBag)
        {
            allResults.AddRange(entry.Results);
            stats.Add(new ProviderCallResult(entry.Provider, entry.Success, entry.ElapsedMs, entry.Results.Count));
        }

        var totalProviderResults = allResults.Count;

        if (request.VerifyLinks)
        {
            // Parallélisation de la vérification des liens (réduit latence = max(timeouts))
            allResults = await VerifyParallelAsync(allResults, request.MaxResults, cancellationToken).ConfigureAwait(false);
        }

        var tagged = allResults.Select(r => new SearchResult
        {
            Title = r.Title,
            Url = r.Url,
            Snippet = r.Snippet,
            Source = r.Source,
            Provider = r.Provider,
            PublishedAt = r.PublishedAt,
            Type = r.Type,
            IsOfficial = r.IsOfficial || _officialDetector.IsOfficialUrl(r.Url),
            ContentType = string.IsNullOrEmpty(r.ContentType) ? LinkTypeClassifier.Detect(r.Url) : r.ContentType,
            ThumbnailUrl = r.ThumbnailUrl,
            Author = r.Author,
            ExtraScore = r.ExtraScore,
            IsVerified = r.IsVerified,
            LinkStatus = r.LinkStatus,
            VideoId = r.VideoId,
            ChannelId = r.ChannelId,
            Duration = r.Duration,
            OpenNow = r.OpenNow,
SubscriberCount = r.SubscriberCount,
                VideoCount = r.VideoCount,
                ChannelPublishedAt = r.ChannelPublishedAt,
                EntityAgreement = r.EntityAgreement,
                Latitude = r.Latitude,
                Longitude = r.Longitude,
                Confidence = TrustScoring.ComputeConfidence(r, r.IsVerified)
        }).Where(r => r.LinkStatus != LinkStatus.Failed).ToList();

        var deduped = _deduper.Dedupe(tagged);
        var merged = _nearDuplicateDetector.Merge(deduped);
        var mergedCount = totalProviderResults - merged.Count;

        // P19.1 — Consolidation d'entités multi-providers : regroupement des chaînes
        // YouTube par identité, choix du représentant (préférence @handle) et
        // score d'accord cross-provider (cohérence avant de classer).
        var consolidated = _entityConsolidator.Consolidate(merged, providers.Count);

        var groups = _grouper.Group(consolidated);
        var ranked = _ranker.Rank(consolidated, request.Query, request.Type, request.PreferOfficial, _fakeSiteDetector).Take(request.MaxResults).ToList();

        var agreement = _consensus.Analyze(ranked).Agreement;

        var response = new SearchResponse
        {
            Query = request.Query,
            Results = ranked,
            ProvidersUsed = providers.Select(p => p.Name).ToList(),
            CompletedAt = DateTimeOffset.UtcNow,
            TotalProviderResults = totalProviderResults,
            MergedCount = Math.Max(0, mergedCount),
            TotalGroups = ranked.Select(r => r.GroupId).Distinct().Count(),
            ConsensusAgreement = agreement,
            WinnerProvider = ranked.FirstOrDefault()?.Provider,
            ProviderStats = stats
        };

        _cache.Set(key, response, TtlFor(request.Type));
        RecordHotQuery(key, request);
        return response;
    }

    public Task<OfficialSiteMatch?> ResolveOfficialAsync(string query, CancellationToken cancellationToken = default)
    {
        var match = _officialDetector.ResolveOfficial(query);
        return Task.FromResult(match);
    }

    public async Task<bool> VerifyLinkAsync(string url, CancellationToken cancellationToken = default)
    {
        var result = await _linkVerifier.VerifyAsync(url, cancellationToken);
        return result.IsValid;
    }

    public void InvalidateCache() => _cache.Invalidate();

    public void InvalidateProvider(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return;
        _cache.InvalidateWhere(k => k.IndexOf("|" + providerName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    k.StartsWith(providerName + ",", StringComparison.OrdinalIgnoreCase));
    }

    public async Task PrefetchHotQueriesAsync(int count, CancellationToken cancellationToken = default)
    {
        List<SearchRequest> requests;
        lock (_hotGate)
        {
            requests = _hotQueries.Values
                .OrderByDescending(v => v.Count)
                .Take(Math.Max(1, count))
                .Select(v => v.Request)
                .ToList();
        }

        foreach (var request in requests)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            _cache.InvalidateWhere(k => k == CacheKey(request));
            await SearchAsync(request, cancellationToken);
        }
    }

    public IReadOnlyDictionary<string, ProviderHealth> GetProviderHealth()
    {
        lock (_healthGate)
            return new Dictionary<string, ProviderHealth>(_health, StringComparer.OrdinalIgnoreCase);
    }

    private void RecordHealth(string provider, bool success, long elapsedMs, int resultCount)
    {
        lock (_healthGate)
        {
            var current = _health.TryGetValue(provider, out var h) ? h : new ProviderHealth(0, 0, 0);
            _health[provider] = new ProviderHealth(
                current.Calls + 1,
                current.Failures + (success ? 0 : 1),
                current.TotalMs + elapsedMs);
        }
    }

    private void RecordHotQuery(string key, SearchRequest request)
    {
        lock (_hotGate)
        {
            if (_hotQueries.TryGetValue(key, out var existing))
                _hotQueries[key] = (existing.Count + 1, existing.Request);
            else
                _hotQueries[key] = (1, request);
        }
    }

    private void RecordHit(string key, SearchRequest request)
    {
        lock (_hotGate)
        {
            if (_hotQueries.TryGetValue(key, out var existing))
                _hotQueries[key] = (existing.Count + 1, existing.Request);
            else
                _hotQueries[key] = (1, request);
        }
    }

    private static SearchResponse Clone(SearchResponse cached, bool fromCache)
        => new()
        {
            Query = cached.Query,
            Results = cached.Results.Select(r => new SearchResult
            {
                Title = r.Title,
                Url = r.Url,
                Snippet = r.Snippet,
                Source = r.Source,
                Provider = r.Provider,
                PublishedAt = r.PublishedAt,
                Type = r.Type,
                IsOfficial = r.IsOfficial,
                ContentType = r.ContentType,
                ThumbnailUrl = r.ThumbnailUrl,
                Author = r.Author,
                ExtraScore = r.ExtraScore,
                Confidence = r.Confidence,
                IsVerified = r.IsVerified,
                LinkStatus = r.LinkStatus,
                FinalScore = r.FinalScore,
                GroupId = r.GroupId,
                MergedCount = r.MergedCount,
                VideoId = r.VideoId,
                ChannelId = r.ChannelId,
                Duration = r.Duration,
                OpenNow = r.OpenNow,
                SubscriberCount = r.SubscriberCount,
                VideoCount = r.VideoCount,
                ChannelPublishedAt = r.ChannelPublishedAt,
                EntityAgreement = r.EntityAgreement,
                Latitude = r.Latitude,
                Longitude = r.Longitude
            }).ToList(),
            ProvidersUsed = cached.ProvidersUsed,
            FromCache = fromCache,
            CompletedAt = cached.CompletedAt,
            TotalProviderResults = cached.TotalProviderResults,
            MergedCount = cached.MergedCount,
            TotalGroups = cached.TotalGroups,
            ConsensusAgreement = cached.ConsensusAgreement,
            WinnerProvider = cached.WinnerProvider,
            ProviderStats = cached.ProviderStats
        };

    private IReadOnlyList<IWebSearchProvider> SelectProviders(SearchRequest request)
    {
        var requested = request.ProviderNames;
        if (requested is { Count: > 0 })
        {
            return _providers.Where(p => requested.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        var suggested = QueryInterpreter.SuggestProviders(request);
        if (suggested.Count > 0)
            return _providers.Where(p => suggested.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        return _providers.Where(p => p.CanHandle(request)).ToList();
    }

    /// <summary>
    /// Variante parallèle : tous les liens candidats sont vérifiés simultanément,
    /// ce qui divise la latence totale par le nombre de candidats.
    /// </summary>
    private async Task<List<SearchResult>> VerifyParallelAsync(List<SearchResult> results, int maxResults, CancellationToken ct)
    {
        var candidates = results.OrderByDescending(r => r.ExtraScore ?? 0).Take(Math.Max(maxResults * 2, 8)).ToList();
        var verifiedBag = new System.Collections.Concurrent.ConcurrentBag<SearchResult>();

        var verifyTasks = candidates.Select(async result =>
        {
            if (_fakeSiteDetector.IsLikelyFake(result.Url))
            {
                result.LinkStatus = LinkStatus.Failed;
                result.IsVerified = false;
                return;
            }

            try
            {
                var check = await _linkVerifier.VerifyAsync(result.Url, ct).ConfigureAwait(false);
                result.IsVerified = check.IsValid;
                result.LinkStatus = check.IsValid ? LinkStatus.VerifiedOk : LinkStatus.Failed;
                if (check.IsValid)
                    verifiedBag.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WebSearch] VerifyAsync failed for {Url}", result.Url);
                result.LinkStatus = LinkStatus.Failed;
                result.IsVerified = false;
            }
        }).ToList();

        await Task.WhenAll(verifyTasks).ConfigureAwait(false);

        foreach (var result in results.Where(r => !candidates.Contains(r)))
            result.LinkStatus = LinkStatus.Skipped;

        return verifiedBag.ToList();
    }

    private TimeSpan TtlFor(SearchResultType type)
    {
        if (_options.PerTypeTtl.TryGetValue(type, out var ttl))
            return ttl;
        return _options.Ttl;
    }

    private static string CacheKey(SearchRequest request)
    {
        var parts = new List<string> { request.Query.Trim().ToLowerInvariant(), request.Type.ToString() };
        if (!string.IsNullOrWhiteSpace(request.Language)) parts.Add(request.Language);
        if (request.ProviderNames is { Count: > 0 }) parts.Add(string.Join(",", request.ProviderNames.OrderBy(x => x)));
        if (request.NearLatitude is { } lat) parts.Add(lat.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        if (request.NearLongitude is { } lon) parts.Add(lon.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        parts.Add($"r:{request.MaxResults}");
        parts.Add($"v:{request.VerifyLinks}");
        return string.Join("|", parts);
    }
}
