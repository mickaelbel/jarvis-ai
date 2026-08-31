using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface ILocalSearchService
{
    Task<SearchIndexResult> BuildIndexAsync(string path, CancellationToken ct = default);
    Task<List<SearchResult>> SearchAsync(string query, string? indexPath = null, int limit = 50, CancellationToken ct = default);
    Task<List<SearchResult>> SearchWithRegexAsync(string pattern, string path, CancellationToken ct = default);
}

public sealed class LocalSearchService : ILocalSearchService
{
    private readonly ILogger<LocalSearchService> _logger;
    private readonly string _indexPath;

    public LocalSearchService(ILogger<LocalSearchService> logger)
    {
        _logger = logger;
        _indexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI", "search_index");
    }

    public async Task<SearchIndexResult> BuildIndexAsync(string path, CancellationToken ct = default)
    {
        var result = new SearchIndexResult { Path = path };
        var index = new Dictionary<string, FileIndexEntry>();

        await Task.Run(() =>
        {
            foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".txt" or ".md" or ".cs" or ".js" or ".py" or ".json" or ".xml" or ".csv" or ".log")
                    {
                        var content = File.ReadAllText(file);
                        index[file] = new FileIndexEntry
                        {
                            Path = file,
                            Content = content,
                            SizeBytes = new FileInfo(file).Length,
                            LastModified = File.GetLastWriteTime(file)
                        };
                        result.FilesIndexed++;
                    }
                }
                catch { }
            }
        }, ct);

        result.Success = true;
        _logger.LogInformation("[Search] Indexed {Count} files from {Path}", result.FilesIndexed, path);
        return result;
    }

    public async Task<List<SearchResult>> SearchAsync(string query, string? indexPath = null, int limit = 50, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var searchPath = indexPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        await Task.Run(() =>
        {
            foreach (var file in Directory.GetFiles(searchPath, "*.*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested || results.Count >= limit) break;

                try
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".txt" or ".md" or ".cs" or ".js" or ".py" or ".json" or ".xml" or ".csv" or ".log")
                    {
                        var content = File.ReadAllText(file);
                        if (content.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            var matchIndex = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                            var contextStart = Math.Max(0, matchIndex - 50);
                            var contextEnd = Math.Min(content.Length, matchIndex + query.Length + 50);
                            var context = content[contextStart..contextEnd].Trim();

                            results.Add(new SearchResult
                            {
                                FilePath = file,
                                MatchContext = context,
                                Score = CalculateRelevance(query, content)
                            });
                        }
                    }
                }
                catch { }
            }
        }, ct);

        return results.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    public async Task<List<SearchResult>> SearchWithRegexAsync(string pattern, string path, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        await Task.Run(() =>
        {
            foreach (var file in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var content = File.ReadAllText(file);
                    var matches = regex.Matches(content);
                    if (matches.Count > 0)
                    {
                        results.Add(new SearchResult
                        {
                            FilePath = file,
                            MatchContext = $"{matches.Count} matches found",
                            Score = matches.Count
                        });
                    }
                }
                catch { }
            }
        }, ct);

        return results.OrderByDescending(r => r.Score).ToList();
    }

    private static double CalculateRelevance(string query, string content)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(query, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += query.Length;
        }
        return count;
    }
}

public sealed class SearchIndexResult
{
    public bool Success { get; set; }
    public string Path { get; set; } = "";
    public int FilesIndexed { get; set; }
}

public sealed class SearchResult
{
    public string FilePath { get; set; } = "";
    public string MatchContext { get; set; } = "";
    public double Score { get; set; }
}

internal class FileIndexEntry
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
}
