using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ICodeSnippetManager
{
    IReadOnlyList<CodeSnippet> GetSnippets(string? language = null, string? tag = null);
    CodeSnippet? GetSnippet(string snippetId);
    string CreateSnippet(string title, string code, string language, List<string>? tags = null, string? description = null);
    void UpdateSnippet(string snippetId, string code, string? title = null);
    void DeleteSnippet(string snippetId);
    IReadOnlyList<CodeSnippet> SearchSnippets(string query);
    IReadOnlyList<string> GetLanguages();
    IReadOnlyList<string> GetTags();
}

public sealed class CodeSnippetManager : ICodeSnippetManager
{
    private readonly ILogger<CodeSnippetManager> _logger;
    private readonly string _storagePath;
    private readonly List<CodeSnippet> _snippets = new();

    public CodeSnippetManager(ILogger<CodeSnippetManager> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "code_snippets.json");
        Load();
    }

    public IReadOnlyList<CodeSnippet> GetSnippets(string? language = null, string? tag = null)
    {
        var query = _snippets.AsEnumerable();

        if (language is not null)
            query = query.Where(s => s.Language.Equals(language, StringComparison.OrdinalIgnoreCase));

        if (tag is not null)
            query = query.Where(s => s.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));

        return query.OrderByDescending(s => s.LastModified ?? s.CreatedAt).ToList();
    }

    public CodeSnippet? GetSnippet(string snippetId)
        => _snippets.FirstOrDefault(s => s.Id == snippetId);

    public string CreateSnippet(string title, string code, string language, List<string>? tags = null, string? description = null)
    {
        var snippet = new CodeSnippet
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Code = code,
            Language = language,
            Description = description ?? "",
            Tags = tags ?? new(),
            CreatedAt = DateTime.UtcNow
        };

        _snippets.Add(snippet);
        Save();
        return snippet.Id;
    }

    public void UpdateSnippet(string snippetId, string code, string? title = null)
    {
        var snippet = GetSnippet(snippetId);
        if (snippet is not null)
        {
            snippet.Code = code;
            if (title is not null) snippet.Title = title;
            snippet.LastModified = DateTime.UtcNow;
            Save();
        }
    }

    public void DeleteSnippet(string snippetId)
    {
        _snippets.RemoveAll(s => s.Id == snippetId);
        Save();
    }

    public IReadOnlyList<CodeSnippet> SearchSnippets(string query)
    {
        return _snippets
            .Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       s.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       s.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       s.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public IReadOnlyList<string> GetLanguages()
        => _snippets.Select(s => s.Language).Distinct().OrderBy(l => l).ToList();

    public IReadOnlyList<string> GetTags()
        => _snippets.SelectMany(s => s.Tags).Distinct().OrderBy(t => t).ToList();

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<CodeSnippet>>(json);
                if (loaded is not null) _snippets.AddRange(loaded);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_snippets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class CodeSnippet
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Code { get; set; } = "";
    public string Language { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModified { get; set; }
}
