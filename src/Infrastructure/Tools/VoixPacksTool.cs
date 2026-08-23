using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Voice;
using System.Net.Http;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Packs de voix : installe n'importe quelle voix Piper de la communauté
/// (rhasspy/piper-voices) par son nom, ex « fr_FR-tom-medium » ou
/// « en_GB-alan-low ». La voix devient disponible après redémarrage.
/// </summary>
public sealed class VoixPacksTool : ITool
{
    private static readonly HttpClient Http = CreateClient();

    public string Name => "installe_voix";
    public string Description =>
        "Installe une nouvelle voix Piper depuis la bibliothèque communautaire HuggingFace. " +
        "Actions : liste (voix disponibles pour une langue), installe (télécharge .onnx + config), supprime. " +
        "Noms du type fr_FR-tom-medium, en_US-amy-low, en_GB-alan-medium…";
    public string Category => "voice";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("action", "liste | installe | supprime", typeof(string), required: true),
        new ToolParameter("nom", "Identifiant de la voix, ex fr_FR-tom-medium (installe/supprime)", typeof(string), required: false),
        new ToolParameter("langue", "Filtre langue pour liste, ex fr ou en (liste)", typeof(string), required: false),
        new ToolParameter("definition", "low | medium | high — qualité souhaitée (installe, défaut medium)", typeof(string), required: false)
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            parameters ??= new Dictionary<string, string>();
            var action = parameters.TryGetValue("action", out var a) ? a.Trim().ToLowerInvariant() : "";
            var dir = VoicePaths.FindPiperDirectory();
            if (dir is null && action != "liste")
                return Task.FromResult(ToolResult.Failed("Répertoire des voix introuvable."));

            return action switch
            {
                "liste" => ListAsync(parameters.TryGetValue("langue", out var l) ? l.Trim() : "fr", cancellationToken),
                "installe" => InstallAsync(
                    parameters.TryGetValue("nom", out var n) ? n.Trim() : "",
                    parameters.TryGetValue("definition", out var q) ? q.Trim().ToLowerInvariant() : "medium",
                    dir!, cancellationToken),
                "supprime" => Task.FromResult(Delete(
                    parameters.TryGetValue("nom", out var s) ? s.Trim() : "", dir!)),
                _ => Task.FromResult(ToolResult.Failed("Action inconnue. Utilise liste, installe ou supprime."))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed($"Erreur packs de voix : {ex.Message}"));
        }
    }

    private static async Task<ToolResult> ListAsync(string languePrefix, CancellationToken ct)
    {
        languePrefix = languePrefix.ToLowerInvariant();
        var url = $"https://huggingface.co/api/models/rhasspy/piper-voices/tree/v1.0.0/{languePrefix}?recursive=true";
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var voix = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("path").GetString() ?? "")
            .Where(p => p.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileNameWithoutExtension(Path.GetFileName(p)))
            .Distinct()
            .OrderBy(v => v)
            .Take(60)
            .ToList();

        return ToolResult.Succeeded(voix.Count == 0
            ? "Aucune voix trouvée pour ce préfixe de langue."
            : $"Voix Piper disponibles ({languePrefix}) : {string.Join(", ", voix)}. Installe avec l'action installe + nom.");
    }

    private static async Task<ToolResult> InstallAsync(string nom, string definition, string dir, CancellationToken ct)
    {
        // fr_FR-tom-medium -> fr/fr_FR/tom/medium
        var parts = nom.Split('-');
        if (parts.Length < 3)
            return ToolResult.Failed("Nom attendu : langue_PAYS-locuteur-qualité, ex fr_FR-tom-medium.");

        var baseUrl = $"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/{parts[0][..2].ToLowerInvariant()}/{parts[0]}/{string.Join('/', parts[1..^1])}/{parts[^1]}";
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var (suffix, tailleMin) in new[] { (".onnx", 5_000_000L), (".onnx.json", 200L) })
            {
                var dest = Path.Combine(dir, nom + suffix);
                if (File.Exists(dest) && new FileInfo(dest).Length >= tailleMin) continue;
                using var resp = await Http.GetAsync(baseUrl + suffix, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    return ToolResult.Failed($"Téléchargement impossible ({resp.StatusCode}) pour {nom}{suffix}. Vérifie le nom exact avec l'action liste.");
                await using var fs = File.Create(dest);
                await resp.Content.CopyToAsync(fs, ct);
            }
            return ToolResult.Succeeded(
                $"Voix « {nom} » installée dans {dir}. Elle sera disponible après redémarrage de Jarvis.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Installation échouée : {ex.Message}");
        }
    }

    private static ToolResult Delete(string nom, string dir)
    {
        if (string.IsNullOrWhiteSpace(nom)) return ToolResult.Failed("Précise le nom de la voix à supprimer.");
        if (!nom.Contains('-')) return ToolResult.Failed("Nom invalide.");
        var supprimes = new List<string>();
        foreach (var suffix in new[] { ".onnx", ".onnx.json" })
        {
            var f = Path.Combine(dir, nom + suffix);
            if (File.Exists(f)) { File.Delete(f); supprimes.Add(nom + suffix); }
        }
        return supprimes.Count == 0
            ? ToolResult.Failed($"Voix « {nom} » introuvable.")
            : ToolResult.Succeeded($"Supprimé : {string.Join(", ", supprimes)}. Effectif après redémarrage.");
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisAI/1.0");
        return c;
    }
}
