using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

// Hub de contenu inspiré de hub_contenu.py / suivi_contenus.py :
// sauvegarde de liens, lecture du texte, et détection de mises à jour
// (hash du contenu) pour les pages suivies.
public sealed class ContentHubTool : ITool
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "content_hub.json");

    private readonly ILogger<ContentHubTool> _logger;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) JarvisAI/1.0" } }
    };

    public ContentHubTool(ILogger<ContentHubTool> logger)
    {
        _logger = logger;
    }

    public string Name => "content_hub";
    public string Description =>
        "Sauvegarde et suis des contenus web : enregistre un lien pour plus tard, liste ta bibliothèque, " +
        "relis le texte extrait d'un lien, et détecte les pages qui ont changé depuis la dernière vérification.";
    public string Category => "web";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "save | list | read | check | remove", typeof(string), required: true),
        new("url", "URL du contenu (save, read, remove)", typeof(string)),
        new("title", "Titre optionnel (save)", typeof(string)),
        new("tags", "Tags séparés par des virgules (save)", typeof(string)),
        new("query", "Filtre par titre/tag/url (list)", typeof(string))
    };

    private sealed class Entry
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public DateTime SavedAt { get; set; }
        public string? LastHash { get; set; }
        public DateTime? LastChecked { get; set; }
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("url", out var url);
        parameters.TryGetValue("title", out var title);
        parameters.TryGetValue("tags", out var tags);
        parameters.TryGetValue("query", out var query);

        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "save" => SaveAsync(url, title, tags, cancellationToken),
                "list" => Task.FromResult(List(query)),
                "read" => ReadAsync(url, cancellationToken),
                "check" => CheckUpdatesAsync(cancellationToken),
                "remove" => Task.FromResult(Remove(url)),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : save, list, read, check, remove"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ContentHub] Action {Action} échouée", action);
            return ToolResult.Failed($"Erreur hub de contenu : {ex.Message}");
        }
    }

    // ── Persistance ──────────────────────────────────────────────────────────
    private static List<Entry> Load()
    {
        if (!File.Exists(StorePath)) return new List<Entry>();
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StorePath)) ?? new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private static void Save(List<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(StorePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string NormalizeUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://" + url;

    // ── Actions ──────────────────────────────────────────────────────────────
    private async Task<ToolResult> SaveAsync(string? url, string? title, string? tags, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Failed("Paramètre 'url' requis.");
        var normalized = NormalizeUrl(url.Trim());
        var entries = Load();
        if (entries.Any(e => e.Url.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return ToolResult.Succeeded($"Ce lien est déjà dans ton hub : {normalized}");

        var fetchedTitle = title;
        string? hash = null;
        try
        {
            var pageText = await FetchTextAsync(normalized, ct);
            if (string.IsNullOrWhiteSpace(fetchedTitle))
            {
                var match = System.Text.RegularExpressions.Regex.Match(pageText, @"<title[^>]*>(.*?)</title>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                if (match.Success) fetchedTitle = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            }
            hash = ComputeHash(pageText);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ContentHub] Impossible de précharger {Url}, sauvegarde quand même", normalized);
        }

        entries.Add(new Entry
        {
            Url = normalized,
            Title = string.IsNullOrWhiteSpace(fetchedTitle) ? normalized : fetchedTitle,
            Tags = string.IsNullOrWhiteSpace(tags)
                ? new List<string>()
                : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            SavedAt = DateTime.Now,
            LastHash = hash
        });
        Save(entries);
        return ToolResult.Succeeded($"Enregistré : « {entries[^1].Title} » → {normalized}. ACTION TERMINÉE.");
    }

    private static ToolResult List(string? query)
    {
        var entries = Load();
        if (entries.Count == 0) return ToolResult.Succeeded("Ton hub de contenu est vide. Utilise action=save pour enregistrer un lien.");

        IEnumerable<Entry> filtered = entries;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.ToLowerInvariant();
            filtered = entries.Where(e =>
                e.Title.ToLowerInvariant().Contains(q) ||
                e.Url.ToLowerInvariant().Contains(q) ||
                e.Tags.Any(t => t.ToLowerInvariant().Contains(q)));
        }

        var list = filtered.OrderByDescending(e => e.SavedAt).Take(30).ToList();
        if (list.Count == 0) return ToolResult.Succeeded($"Aucun contenu ne correspond à « {query} ».");

        var sb = new System.Text.StringBuilder($"HUB DE CONTENU ({entries.Count} éléments) :\n");
        foreach (var e in list)
        {
            sb.AppendLine($"  • {e.Title}");
            sb.AppendLine($"    {e.Url}  [sauvé le {e.SavedAt:dd/MM/yyyy}]" + (e.Tags.Count > 0 ? $"  #{string.Join(" #", e.Tags)}" : ""));
        }
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<ToolResult> ReadAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Failed("Paramètre 'url' requis.");
        var text = await FetchTextAsync(NormalizeUrl(url.Trim()), ct);
        return ToolResult.Succeeded(text.Length > 6000 ? text[..6000] + "\n…(tronqué)" : text);
    }

    private async Task<ToolResult> CheckUpdatesAsync(CancellationToken ct)
    {
        var entries = Load();
        if (entries.Count == 0) return ToolResult.Succeeded("Rien à vérifier, le hub est vide.");
        var updated = new List<string>();
        foreach (var entry in entries.Take(20))
        {
            try
            {
                var text = await FetchTextAsync(entry.Url, ct);
                var hash = ComputeHash(text);
                var changed = entry.LastHash is not null && entry.LastHash != hash;
                entry.LastHash = hash;
                entry.LastChecked = DateTime.Now;
                if (changed) updated.Add(entry.Title);
                await Task.Delay(300, ct); // politesse HTTP
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ContentHub] Check échoué pour {Url}", entry.Url);
            }
        }
        Save(entries);
        return updated.Count == 0
            ? ToolResult.Succeeded("Vérification faite : aucun de tes contenus suivis n'a changé.")
            : ToolResult.Succeeded($"{updated.Count} contenu(s) mis(s) à jour depuis la dernière vérification :\n" +
                                    string.Join("\n", updated.Select(t => $"  • {t}")));
    }

    private static ToolResult Remove(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Failed("Paramètre 'url' requis.");
        var entries = Load();
        var normalized = NormalizeUrl(url.Trim());
        var removed = entries.RemoveAll(e => e.Url.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return ToolResult.Failed($"Lien introuvable dans le hub : {normalized}");
        Save(entries);
        return ToolResult.Succeeded("Lien retiré du hub. ACTION TERMINÉE.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private async Task<string> FetchTextAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<script[\s\S]*?</script>|<style[\s\S]*?</style>", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        return System.Text.RegularExpressions.Regex.Replace(html, @"\s+", " ").Trim();
    }

    private static string ComputeHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}
