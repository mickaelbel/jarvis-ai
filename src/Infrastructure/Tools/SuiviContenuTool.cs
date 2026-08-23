using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class SuiviContenuTool : ITool
{
    private readonly ILogger<SuiviContenuTool> _logger;
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "contenus.json");

    private static readonly string[] Statuts = { "idee", "script", "tournage", "montage", "publie" };
    private static readonly Dictionary<string, string> Synonymes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idée"] = "idee", ["idées"] = "idee", ["idea"] = "idee",
        ["écriture"] = "script", ["script"] = "script", ["écrit"] = "script",
        ["tournage"] = "tournage", ["tourne"] = "tournage", ["shoot"] = "tournage",
        ["montage"] = "montage", ["édit"] = "montage", ["edit"] = "montage",
        ["publié"] = "publie", ["publie"] = "publie", ["published"] = "publie", ["posté"] = "publie"
    };

    public SuiviContenuTool(ILogger<SuiviContenuTool> logger) => _logger = logger;

    public string Name => "suivi";
    public string Description =>
        "Pipeline vidéo : idée → script → tournage → montage → publié. " +
        "Actions : nouvelle, statut, oujenesuis, liste. Deadline AAAA-MM-JJ optionnelle.";
    public string Category => "content";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "nouvelle | statut | oujenesuis | liste", typeof(string), required: true),
        new("titre", "Titre de la vidéo", typeof(string)),
        new("statut", "Nouveau statut (idee/script/tournage/montage/publie)", typeof(string)),
        new("deadline", "Date limite AAAA-MM-JJ", typeof(string)),
        new("plateforme", "YouTube / TikTok / Insta / Reels / Shorts", typeof(string)),
        new("notes", "Notes libres", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "nouvelle" => NouvelleAsync(parameters, ct),
                "statut" => StatutAsync(parameters, ct),
                "oujenesuis" => OuJEnSuisAsync(ct),
                "liste" => ListeAsync(parameters, ct),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : nouvelle, statut, oujenesuis, liste"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Suivi] {Action} échoué", action);
            return ToolResult.Failed($"Erreur suivi : {ex.Message}");
        }
    }

    private async Task<ToolResult> NouvelleAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var titre = p.GetValueOrDefault("titre");
        if (string.IsNullOrWhiteSpace(titre)) return ToolResult.Failed("Paramètre titre requis.");
        var entries = Load();
        var entry = new ContenuEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Titre = titre.Trim(),
            Statut = "idee",
            Plateforme = p.GetValueOrDefault("plateforme") ?? "YouTube",
            Deadline = DateTime.TryParse(p.GetValueOrDefault("deadline"), out var d) ? d : (DateTime?)null,
            Notes = p.GetValueOrDefault("notes") ?? "",
            CreeLe = DateTime.Now
        };
        entries.Add(entry);
        Save(entries);
        AppendIdeesMd(entry);
        return ToolResult.Succeeded($"Nouvelle idée « {titre} » créée (statut: idée). ACTION TERMINÉE.");
    }

    private async Task<ToolResult> StatutAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var titre = p.GetValueOrDefault("titre");
        var newStatut = p.GetValueOrDefault("statut");
        if (string.IsNullOrWhiteSpace(titre) || string.IsNullOrWhiteSpace(newStatut))
            return ToolResult.Failed("Paramètres titre et statut requis.");
        var norm = NormalizeStatut(newStatut);
        if (norm is null) return ToolResult.Failed($"Statut invalide. Valides : {string.Join(", ", Statuts)}");

        var entries = Load();
        var match = entries.FirstOrDefault(e => e.Titre.Contains(titre, StringComparison.OrdinalIgnoreCase));
        if (match is null) return ToolResult.Failed($"Aucune vidéo ne correspond à « {titre} ».");
        match.Statut = norm;
        Save(entries);
        return ToolResult.Succeeded($"« {match.Titre} » → {norm}. ACTION TERMINÉE.");
    }

    private async Task<ToolResult> OuJEnSuisAsync(CancellationToken ct)
    {
        var entries = Load();
        if (entries.Count == 0) return ToolResult.Succeeded("Pipeline vide. Ajoute une idée avec action=nouvelle.");

        var counts = entries.GroupBy(e => e.Statut).ToDictionary(g => g.Key, g => g.Count());
        var sb = new System.Text.StringBuilder("PIPELINE VIDÉO :\n");
        foreach (var s in Statuts)
            sb.AppendLine($"  {s,-10} : {counts.GetValueOrDefault(s, 0)}");

        var overdue = entries.Where(e => e.Deadline.HasValue && e.Deadline < DateTime.Today && e.Statut != "publie").ToList();
        var soon = entries.Where(e => e.Deadline.HasValue && e.Deadline >= DateTime.Today && e.Deadline <= DateTime.Today.AddDays(7) && e.Statut != "publie").ToList();

        if (overdue.Count > 0)
        {
            sb.AppendLine("\n⚠ EN RETARD :");
            foreach (var e in overdue) sb.AppendLine($"  • {e.Titre} (deadline {e.Deadline:dd/MM}) — {e.Statut}");
        }
        if (soon.Count > 0)
        {
            sb.AppendLine("\n📅 PROCHAINES ÉCHÉANCES (7j) :");
            foreach (var e in soon) sb.AppendLine($"  • {e.Titre} (deadline {e.Deadline:dd/MM}) — {e.Statut}");
        }

        // Cross-check Google Agenda would require calling agenda tool — skip for now
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<ToolResult> ListeAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var entries = Load();
        var filtre = p.GetValueOrDefault("statut");
        if (!string.IsNullOrWhiteSpace(filtre))
        {
            var norm = NormalizeStatut(filtre);
            if (norm is not null) entries = entries.Where(e => e.Statut == norm).ToList();
        }
        if (entries.Count == 0) return ToolResult.Succeeded("Aucun contenu ne correspond.");
        var sb = new System.Text.StringBuilder();
        foreach (var e in entries.OrderByDescending(x => x.CreeLe).Take(20))
            sb.AppendLine($"  • [{e.Statut}] {e.Titre}  ({e.Plateforme})" + (e.Deadline.HasValue ? $"  ⏰ {e.Deadline:dd/MM}" : ""));
        return ToolResult.Succeeded(sb.ToString());
    }

    private static string? NormalizeStatut(string s)
    {
        if (Synonymes.TryGetValue(s.Trim(), out var n)) return n;
        return Statuts.Contains(s.ToLowerInvariant()) ? s.ToLowerInvariant() : null;
    }

    private static List<ContenuEntry> Load()
    {
        if (!File.Exists(StorePath)) return new List<ContenuEntry>();
        try { return JsonSerializer.Deserialize<List<ContenuEntry>>(File.ReadAllText(StorePath)) ?? new List<ContenuEntry>(); }
        catch { return new List<ContenuEntry>(); }
    }

    private static void Save(List<ContenuEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(StorePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AppendIdeesMd(ContenuEntry e)
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        var mdPath = Path.Combine(dir, "notes", "idees.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);
        File.AppendAllText(mdPath, $"\n## {e.Titre} ({e.CreeLe:dd/MM/yyyy})\nStatut: {e.Statut}\nPlateforme: {e.Plateforme}\nDeadline: {e.Deadline?.ToString("yyyy-MM-dd") ?? "—"}\nNotes: {e.Notes}\n---\n");
    }

    private sealed class ContenuEntry
    {
        public string Id { get; set; } = "";
        public string Titre { get; set; } = "";
        public string Statut { get; set; } = "idee";
        public string Plateforme { get; set; } = "YouTube";
        public DateTime? Deadline { get; set; }
        public string Notes { get; set; } = "";
        public DateTime CreeLe { get; set; }
    }
}




