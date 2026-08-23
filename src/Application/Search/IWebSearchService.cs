namespace JarvisAI.Application.Search;

public interface IWebSearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
    Task<OfficialSiteMatch?> ResolveOfficialAsync(string query, CancellationToken cancellationToken = default);
    Task<bool> VerifyLinkAsync(string url, CancellationToken cancellationToken = default);
    void InvalidateCache();
    void InvalidateProvider(string providerName);
    Task PrefetchHotQueriesAsync(int count, CancellationToken cancellationToken = default);
    IReadOnlyDictionary<string, ProviderHealth> GetProviderHealth();
}
