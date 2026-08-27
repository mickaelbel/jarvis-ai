using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// RAG sur fichiers locaux : indexe le contenu de dossiers (txt/md/code/pdf)
/// dans la mémoire vectorielle, puis recherche sémantique instantanée.
/// Les extraits pertinents alimentent aussi automatiquement le rappel épisodique.
/// </summary>
public sealed class IndexerFichiersTool : ITool
{
    private static readonly string[] Extensions = { ".txt", ".md", ".cs", ".py", ".js", ".ts", ".json", ".xml", ".html", ".csv", ".log", ".pdf" };
    private const int TailleChunk = 800;

    private readonly IMemoryService _memory;
    private readonly IMemorySettingsStore? _settingsStore;

    public IndexerFichiersTool(IMemoryService memory, IMemorySettingsStore? settingsStore = null)
    {
        _memory = memory;
        _settingsStore = settingsStore;
    }

    public string Name => "indexer_fichiers";
    public string Description =>
        "Indexe des fichiers ou dossiers locaux (txt, md, code, pdf…) en mémoire vectorielle puis répond à des questions sur leur contenu. " +
        "Actions : indexe (chemin), cherche (requête), statut.";
    public string Category => "memory";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("action", "indexe | cherche | statut", typeof(string), required: true),
        new ToolParameter("chemin", "Fichier ou dossier à indexer (indexe)", typeof(string), required: false),
        new ToolParameter("requete", "Texte recherché sémantiquement (cherche)", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            parameters ??= new Dictionary<string, string>();
            var action = parameters.TryGetValue("action", out var a) ? a.Trim().ToLowerInvariant() : "";

            if (action == "statut")
            {
                var entrees = await _memory.SearchAsync(
                    new MemoryQuery { Category = "fichiers", Limit = 1 }, cancellationToken);
                return ToolResult.Succeeded(entrees.Count > 0
                    ? "Des fichiers sont indexés en mémoire."
                    : "Aucun fichier indexé. Utilise l'action indexe avec un chemin.");
            }

            if (action == "cherche")
            {
                var requete = parameters.TryGetValue("requete", out var r) ? r.Trim() : "";
                if (requete.Length < 2)
                    return ToolResult.Failed("Précise la requête de recherche.");
                IReadOnlyList<MemoryEntry> resultats;
                try
                {
                    resultats = await _memory.SearchSemanticAsync(requete, 6, category: "fichiers", cancellationToken: cancellationToken);
                }
                catch
                {
                    resultats = await _memory.SearchAsync(
                        new MemoryQuery { Category = "fichiers", TextSearch = requete, Limit = 6 }, cancellationToken);
                }
                if (resultats.Count == 0)
                    return ToolResult.Succeeded("Aucun extrait pertinent trouvé dans les fichiers indexés.");
                return ToolResult.Succeeded("Extraits trouvés :\n" + string.Join("\n---\n",
                    resultats.Select(e => $"[{e.Key}] {Truncate(e.Content, 500)}")));
            }

            if (action == "indexe")
            {
                if (_settingsStore?.Get() is { MemoryEnabled: false })
                    return ToolResult.Failed("Mémoire désactivée : aucune nouvelle indexation n'est possible tant que « Mémoire activée » est désactivé.");
                var chemin = parameters.TryGetValue("chemin", out var c) ? c.Trim().Trim('"') : "";
                if (chemin.Length == 0 || (!Directory.Exists(chemin) && !File.Exists(chemin)))
                    return ToolResult.Failed("Chemin introuvable : précise un fichier ou un dossier existant.");

                var fichiers = Directory.Exists(chemin)
                    ? Directory.EnumerateFiles(chemin, "*.*", SearchOption.AllDirectories)
                        .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Where(f => !f.Contains("\\.venv\\") && !f.Contains("\\node_modules\\") && !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\.git\\"))
                        .Take(400).ToList()
                    : new List<string> { chemin };

                var morceaux = 0;
                foreach (var f in fichiers)
                {
                    var texte = LireTexte(f);
                    if (string.IsNullOrWhiteSpace(texte)) continue;
                    for (var i = 0; i * TailleChunk < texte.Length; i++)
                    {
                        var chunk = texte.Substring(i * TailleChunk, Math.Min(TailleChunk, texte.Length - i * TailleChunk)).Trim();
                        if (chunk.Length < 40) break;
                        var cle = $"fichier.{CleHash(f)}.{i:D3}";
                        await _memory.SaveMemoryAsync(
                            cle,
                            $"{Path.GetFileName(f)} : {chunk}",
                            MemoryType.Knowledge,
                            "fichiers",
                            importance: 0.4f,
                            tier: MemoryTier.LongTerm,
                            metadata: new Dictionary<string, string> { ["path"] = f, ["chunk"] = i.ToString() },
                            cancellationToken: cancellationToken);
                        morceaux++;
                        if (morceaux >= 3000)
                            return ToolResult.Succeeded($"Indexation interrompue à 3000 extraits (limite). {morceaux} extraits indexés.");
                    }
                }
                return ToolResult.Succeeded(morceaux == 0
                    ? "Rien à indexer (formats non supportés ou fichiers vides)."
                    : $"{morceaux} extraits indexés depuis {fichiers.Count} fichier(s). Tu peux maintenant chercher avec l'action cherche.");
            }

            return ToolResult.Failed("Action inconnue. Utilise indexe, cherche ou statut.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur indexation : {ex.Message}");
        }
    }

    /// <summary>Extraction texte brute : UTF-8/ANSI pour le texte, PdfPig pour les PDF.</summary>
    private static string? LireTexte(string fichier)
    {
        try
        {
            if (Path.GetExtension(fichier).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                using var doc = PdfDocument.Open(fichier);
                foreach (var page in doc.GetPages().Take(80))
                    sb.AppendLine(page.Text);
                return sb.ToString();
            }
            return File.ReadAllText(fichier);
        }
        catch
        {
            return null;
        }
    }

    internal static string CleHash(string chemin)
    {
        var octets = MD5.HashData(Encoding.UTF8.GetBytes(chemin.ToLowerInvariant()));
        return Convert.ToHexString(octets)[..10];
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
