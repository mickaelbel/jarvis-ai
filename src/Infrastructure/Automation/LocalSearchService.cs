using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface ILocalSearchService
{
    Task<SearchIndexResult> BuildIndexAsync(string path, CancellationToken ct = default);
    Task<List<SearchResult>> SearchAsync(string query, string? indexPath = null, int limit = 50, CancellationToken ct = default);
    Task<List<SearchResult>> SearchWithRegexAsync(string pattern, string path, CancellationToken ct = default);
    Task<SearchIndexResult> ClearIndexAsync(string path, CancellationToken ct = default);
}

public sealed class LocalSearchService : ILocalSearchService
{
    private readonly ILogger<LocalSearchService> _logger;
    private readonly string _indexPath;

    public LocalSearchService(ILogger<LocalSearchService> logger)
    {
        _logger = logger;
        _indexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI", "search_index");
        Directory.CreateDirectory(_indexPath);
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

        await PersistIndexAsync(path, index.Values.ToList(), ct);

        result.Success = true;
        _logger.LogInformation("[Search] Indexé {Count} fichiers depuis {Path}", result.FilesIndexed, path);
        return result;
    }

    public async Task<List<SearchResult>> SearchAsync(string query, string? indexPath = null, int limit = 50, CancellationToken ct = default)
    {
        var searchPath = indexPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var indexFile = GetIndexFile(searchPath);

        if (File.Exists(indexFile))
        {
            return await SearchFromIndexAsync(query, indexFile, limit, ct);
        }

        var results = await SearchLiveAsync(query, searchPath, limit, ct);

        var entries = new List<FileIndexEntry>();
        foreach (var file in Directory.GetFiles(searchPath, "*.*", SearchOption.AllDirectories))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".txt" or ".md" or ".cs" or ".js" or ".py" or ".json" or ".xml" or ".csv" or ".log")
                {
                    entries.Add(new FileIndexEntry
                    {
                        Path = file,
                        Content = File.ReadAllText(file),
                        SizeBytes = new FileInfo(file).Length,
                        LastModified = File.GetLastWriteTime(file)
                    });
                }
            }
            catch { }
        }

        await PersistIndexAsync(searchPath, entries, ct);

        return results;
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
                            MatchContext = $"{matches.Count} correspondances trouvées",
                            Score = matches.Count
                        });
                    }
                }
                catch { }
            }
        }, ct);

        return results.OrderByDescending(r => r.Score).ToList();
    }

    public Task<SearchIndexResult> ClearIndexAsync(string path, CancellationToken ct = default)
    {
        var indexFile = GetIndexFile(path);
        var result = new SearchIndexResult { Path = path };

        if (File.Exists(indexFile))
        {
            File.Delete(indexFile);
            _logger.LogInformation("[Search] Index supprimé pour {Path}", path);
        }

        result.Success = true;
        return Task.FromResult(result);
    }

    private async Task<List<SearchResult>> SearchFromIndexAsync(string query, string indexFile, int limit, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(indexFile, ct);
        var entries = JsonSerializer.Deserialize<List<FileIndexEntry>>(json) ?? new List<FileIndexEntry>();
        var results = new List<SearchResult>();

        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested) break;

            var count = 0;
            var idx = 0;
            while ((idx = entry.Content.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                count++;
                idx += query.Length;
            }

            if (count > 0)
            {
                var matchIndex = entry.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                var contextStart = Math.Max(0, matchIndex - 50);
                var contextEnd = Math.Min(entry.Content.Length, matchIndex + query.Length + 50);
                var context = entry.Content[contextStart..contextEnd].Trim();

                results.Add(new SearchResult
                {
                    FilePath = entry.Path,
                    MatchContext = context,
                    Score = count
                });
            }
        }

        return results.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    private async Task<List<SearchResult>> SearchLiveAsync(string query, string searchPath, int limit, CancellationToken ct)
    {
        var results = new List<SearchResult>();

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

    private async Task PersistIndexAsync(string path, List<FileIndexEntry> entries, CancellationToken ct)
    {
        try
        {
            var indexFile = GetIndexFile(path);
            var json = JsonSerializer.Serialize(entries);
            await File.WriteAllTextAsync(indexFile, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Search] Échec de sauvegarde de l'index pour {Path}", path);
        }
    }

    private string GetIndexFile(string path)
    {
        var full = Path.GetFullPath(path);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        var hex = Convert.ToHexString(hashBytes)[..16];
        return Path.Combine(_indexPath, $"index_{hex}.json");
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
