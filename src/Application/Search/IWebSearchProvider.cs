namespace JarvisAI.Application.Search;

public interface IWebSearchProvider
{
    string Name { get; }
    bool CanHandle(SearchRequest request);
    Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}
