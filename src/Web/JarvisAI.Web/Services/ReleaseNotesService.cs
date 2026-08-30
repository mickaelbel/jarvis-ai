using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IReleaseNotesService
{
    string GenerateReleaseNotes(string version, List<string> commitMessages, List<string>? prTitles = null);
    IReadOnlyList<ReleaseNotes> GetSavedNotes();
    void SaveNotes(string version, string content);
    string GetLatestNotes();
}

public sealed class ReleaseNotesService : IReleaseNotesService
{
    private readonly ILogger<ReleaseNotesService> _logger;
    private readonly string _storagePath;
    private readonly List<ReleaseNotes> _notes = new();

    public ReleaseNotesService(ILogger<ReleaseNotesService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "release_notes.json");
        Load();
    }

    public string GenerateReleaseNotes(string version, List<string> commitMessages, List<string>? prTitles = null)
    {
        var features = new List<string>();
        var fixes = new List<string>();
        var other = new List<string>();

        foreach (var msg in commitMessages)
        {
            var lower = msg.ToLowerInvariant();

            if (lower.StartsWith("feat:") || lower.StartsWith("feature:"))
                features.Add(ExtractDescription(msg));
            else if (lower.StartsWith("fix:") || lower.StartsWith("bugfix:"))
                fixes.Add(ExtractDescription(msg));
            else if (!lower.StartsWith("chore:") && !lower.StartsWith("style:"))
                other.Add(ExtractDescription(msg));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Release Notes - v{version}");
        sb.AppendLine();
        sb.AppendLine($"**Date:** {DateTime.UtcNow:dd/MM/yyyy}");
        sb.AppendLine();

        if (features.Any())
        {
            sb.AppendLine("## ✨ Nouveautés");
            foreach (var feat in features)
                sb.AppendLine($"- {feat}");
            sb.AppendLine();
        }

        if (fixes.Any())
        {
            sb.AppendLine("## 🐛 Corrections");
            foreach (var fix in fixes)
                sb.AppendLine($"- {fix}");
            sb.AppendLine();
        }

        if (other.Any())
        {
            sb.AppendLine("## 📝 Autres changements");
            foreach (var item in other)
                sb.AppendLine($"- {item}");
            sb.AppendLine();
        }

        if (prTitles?.Any() == true)
        {
            sb.AppendLine("## 🔗 Pull Requests");
            foreach (var pr in prTitles)
                sb.AppendLine($"- {pr}");
            sb.AppendLine();
        }

        var content = sb.ToString();

        // Auto-save
        SaveNotes(version, content);

        return content;
    }

    public IReadOnlyList<ReleaseNotes> GetSavedNotes()
        => _notes.OrderByDescending(n => n.Version).ToList();

    public void SaveNotes(string version, string content)
    {
        var existing = _notes.FirstOrDefault(n => n.Version == version);
        if (existing is not null)
            _notes.Remove(existing);

        _notes.Add(new ReleaseNotes
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Version = version,
            Content = content,
            CreatedAt = DateTime.UtcNow
        });

        Save();
    }

    public string GetLatestNotes()
        => _notes.OrderByDescending(n => n.CreatedAt).FirstOrDefault()?.Content ?? "Aucune note de release disponible.";

    private string ExtractDescription(string commitMessage)
    {
        var prefixes = new[] { "feat:", "feature:", "fix:", "bugfix:", "chore:", "docs:", "style:", "refactor:" };
        var result = commitMessage;

        foreach (var prefix in prefixes)
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[prefix.Length..].Trim();
                break;
            }
        }

        return char.ToUpper(result[0]) + result[1..];
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<ReleaseNotes>>(json);
                if (loaded is not null) _notes.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_notes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class ReleaseNotes
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
