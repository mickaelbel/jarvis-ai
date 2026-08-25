using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Outil <c>changer_modele</c> : l'IA de base peut proposer n'importe quel
/// modèle Ollama (pas de liste figée), vérifier s'il est installé, annoncer
/// sa taille, le télécharger après accord explicite, puis l'activer pour les
/// requêtes rapides et/ou de raisonnement. Le choix est persistant.
/// </summary>
public sealed partial class ModelChangeTool : ITool
{
    private const string OllamaBase = "http://localhost:11434";
    private const string RegistryManifests = "https://registry.ollama.ai/v2/";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{1,79}$")]
    private static partial Regex ModelNameRegex();

    private readonly ModelOverrideStore _overrides;
    private readonly ModelRouterOptions _defaults;
    private readonly HttpClient _tagsHttp;
    private readonly HttpClient _pullHttp;

    public string Name => "changer_modele";
    public string Description =>
        "Gère le modèle LLM d'Ollama : liste les modèles installés, propose n'importe quel modèle " +
        "(installé ou à télécharger avec accord de l'utilisateur), l'installe puis l'active. " +
        "Utilise-le quand la tâche dépasse tes capacités actuelles ou quand l'utilisateur demande un autre modèle. " +
        "Actions: liste, proposer (vérifie + annonce la taille, demande confirmation), installer (confirmed=true obligatoire), activer, reinitialiser.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public bool McpExpose => true;
    public string? WaitingPhrase => null;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "liste | proposer | installer | activer | reinitialiser", typeof(string), required: true),
        new ToolParameter("modele", "Nom exact du modèle Ollama, ex: qwen3:8b, llama3.3:70b, deepseek-r1:14b (proposer/installer/activer)", typeof(string)),
        new ToolParameter("niveau", "Sur quel profil activer le modèle: rapide | raisonnement | tous (défaut tous)", typeof(string)),
        new ToolParameter("confirmed", "true uniquement après accord explicite de l'utilisateur pour un téléchargement (installer)", typeof(string))
    };

    public ModelChangeTool(ModelOverrideStore overrides, ModelRouterOptions defaults)
    {
        _overrides = overrides;
        _defaults = defaults;
        _tagsHttp = new HttpClient { BaseAddress = new Uri(OllamaBase), Timeout = TimeSpan.FromSeconds(15) };
        _pullHttp = new HttpClient { BaseAddress = new Uri(OllamaBase), Timeout = TimeSpan.FromMinutes(45) };
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var action = parameters.TryGetValue("action", out var a) ? a.Trim().ToLowerInvariant() : "";
        try
        {
            return action switch
            {
                "liste" => await ListeAsync(cancellationToken),
                "proposer" => await ProposerAsync(parameters, cancellationToken),
                "installer" => await InstallerAsync(parameters, cancellationToken),
                "activer" => await ActiverAsync(parameters, cancellationToken),
                "reinitialiser" => Reinitialiser(),
                _ => ToolResult.Failed("Action inconnue. Actions valides : liste, proposer, installer, activer, reinitialiser.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Failed("Opération annulée.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur changer_modele : {ex.Message}");
        }
    }

    private async Task<ToolResult> ListeAsync(CancellationToken ct)
    {
        var locaux = await GetLocalModelsAsync(ct);
        var lignes = locaux.Select(m => $"- {m.name} ({FormatSize(m.size)})");
        var resume =
            $"Modèle rapide actuel : {EffectiveFast}\n" +
            $"Modèle de raisonnement actuel : {EffectiveReasoning}\n" +
            (locaux.Count > 0
                ? $"Installés :\n{string.Join("\n", lignes)}"
                : "Aucun modèle installé détecté (Ollama injoignable ou vide).");
        return ToolResult.Succeeded(resume);
    }

    private async Task<ToolResult> ProposerAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!TryGetModele(parameters, out var modele, out var erreur))
            return ToolResult.Failed(erreur!);

        var niveau = ParseNiveau(parameters);
        var locaux = await GetLocalModelsAsync(ct);
        var installe = locaux.FirstOrDefault(m => m.name.Equals(modele, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(installe.name))
        {
            return ToolResult.Succeeded(
                $"Le modèle {installe.name} ({FormatSize(installe.size)}) est déjà installé. " +
                $"Demande à l'utilisateur : « Veux-tu que je l'active ? » puis appelle activer avec modele={installe.name} et niveau={NiveauTexte(niveau)}.");
        }

        var taille = await GetRegistrySizeAsync(modele, ct);
        var tailleTxt = taille is > 0 ? FormatSize(taille.Value) : "taille inconnue";
        return ToolResult.Succeeded(
            $"Propose ce plan à l'utilisateur en une phrase : « Je peux passer au modèle {modele} (~{tailleTxt}). " +
            $"Je le télécharge ? » Si l'utilisateur accepte, appelle installer avec modele={modele}, niveau={NiveauTexte(niveau)} et confirmed=true.");
    }

    private async Task<ToolResult> InstallerAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!TryGetModele(parameters, out var modele, out var erreur))
            return ToolResult.Failed(erreur!);

        var confirmed = parameters.TryGetValue("confirmed", out var c) &&
                        string.Equals(c.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (!confirmed)
            return ToolResult.Failed(
                "Téléchargement refusé : confirmation explicite requise. " +
                "Demande d'abord l'accord de l'utilisateur, puis rappelle installer avec confirmed=true.");

        // Déjà installé → rien à télécharger.
        var locaux = await GetLocalModelsAsync(ct);
        if (locaux.Any(m => m.name.Equals(modele, StringComparison.OrdinalIgnoreCase)))
            return await ActiverApresInstallationAsync(modele, parameters, "déjà installé", ct);

        var tailleAvant = locaux.Count > 0 ? locaux.Sum(m => m.size) : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pull")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = modele, stream = true }), Encoding.UTF8, "application/json")
        };
        using var response = await _pullHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
            return ToolResult.Failed($"Téléchargement impossible (HTTP {(int)response.StatusCode}). Vérifie le nom du modèle (« {modele} »).");

        string? dernierStatut = null;
        using (var stream = await response.Content.ReadAsStreamAsync(ct))
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync(ct) is { } ligne)
            {
                if (string.IsNullOrWhiteSpace(ligne)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(ligne);
                    dernierStatut = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (doc.RootElement.TryGetProperty("error", out var e))
                        return ToolResult.Failed($"Téléchargement échoué : {e.GetString()}");
                }
                catch { }
            }
        }

        var apres = await GetLocalModelsAsync(ct);
        var delta = apres.Sum(m => m.size) - tailleAvant;
        if (!apres.Any(m => m.name.Equals(modele, StringComparison.OrdinalIgnoreCase)) && delta <= 0 && !string.Equals(dernierStatut, "success", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Failed($"Téléchargement non confirmé (dernier statut : {dernierStatut ?? "aucun"}).");

        return await ActiverApresInstallationAsync(modele, parameters, FormatSize(delta > 0 ? delta : 0), ct);
    }

    private async Task<ToolResult> ActiverAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        if (!TryGetModele(parameters, out var modele, out var erreur))
            return ToolResult.Failed(erreur!);

        var locaux = await GetLocalModelsAsync(ct);
        if (!locaux.Any(m => m.name.Equals(modele, StringComparison.OrdinalIgnoreCase)))
            return ToolResult.Failed(
                $"Le modèle « {modele} » n'est pas installé. Appelle d'abord proposer avec modele={modele} pour organiser le téléchargement.");

        return await ActiverApresInstallationAsync(modele, parameters, "déjà installé", ct);
    }

    private ToolResult Reinitialiser()
    {
        _overrides.Reset();
        return ToolResult.Succeeded($"Modèles réinitialisés : rapide={_defaults.FastModel}, raisonnement={_defaults.ReasoningModel}. Choix persisté.");
    }

    // ---- internes ----

    private string EffectiveFast => _overrides.FastOverride is { Length: > 0 } f ? f : _defaults.FastModel;
    private string EffectiveReasoning => _overrides.ReasoningOverride is { Length: > 0 } r ? r : _defaults.ReasoningModel;

    private async Task<ToolResult> ActiverApresInstallationAsync(string modele, IReadOnlyDictionary<string, string> parameters, string origine, CancellationToken ct)
    {
        var niveau = ParseNiveau(parameters);
        switch (niveau)
        {
            case Niveau.Rapide:
                _overrides.Set(modele, null);
                break;
            case Niveau.Raisonnement:
                _overrides.Set(null, modele);
                break;
            default:
                _overrides.Set(modele, modele);
                break;
        }
        await PreloadSilencieuxAsync(modele, ct);
        return ToolResult.Succeeded(
            $"Modèle {origine} et activé : {modele} sur le profil {NiveauTexte(niveau)}. " +
            $"Effectifs — rapide : {EffectiveFast}, raisonnement : {EffectiveReasoning}. Annonce-le simplement à l'utilisateur.");
    }

    private async Task<List<(string name, long size)>> GetLocalModelsAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _tagsHttp.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var resultat = new List<(string, long)>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var nom = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (nom.Length == 0) continue;
                    var taille = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0L;
                    resultat.Add((nom, taille));
                }
            }
            return resultat;
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Taille de téléchargement via le manifeste du registre officiel.
    /// Best-effort : renvoie null si le registre est inaccessible.</summary>
    private static async Task<long?> GetRegistrySizeAsync(string modele, CancellationToken ct)
    {
        try
        {
            var sep = modele.LastIndexOf(':');
            var repo = sep > 0 ? modele[..sep] : modele;
            var tag = sep > 0 ? modele[(sep + 1)..] : "latest";
            if (!repo.Contains('/')) repo = "library/" + repo;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{RegistryManifests}{repo}/manifests/{tag}");
            request.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            long total = 0;
            if (doc.RootElement.TryGetProperty("config", out var cfg) && cfg.TryGetProperty("size", out var cs))
                total += cs.GetInt64();
            if (doc.RootElement.TryGetProperty("layers", out var layers))
                foreach (var layer in layers.EnumerateArray())
                    if (layer.TryGetProperty("size", out var ls)) total += ls.GetInt64();
            return total;
        }
        catch
        {
            return null;
        }
    }

    private async Task PreloadSilencieuxAsync(string modele, CancellationToken ct)
    {
        try
        {
            await _tagsHttp.PostAsJsonAsync("/api/generate", new { model = modele, prompt = "", stream = false, keep_alive = "30m" }, ct);
        }
        catch { }
    }

    private static bool TryGetModele(IReadOnlyDictionary<string, string> parameters, out string modele, out string? erreur)
    {
        modele = parameters.TryGetValue("modele", out var m) ? m.Trim() : "";
        if (modele.Length == 0)
        {
            erreur = "Paramètre « modele » manquant. Exemples : qwen3:8b, llama3.3:70b, deepseek-r1:14b.";
            return false;
        }
        if (!ModelNameRegex().IsMatch(modele))
        {
            erreur = $"Nom de modèle invalide : « {modele} ». Format attendu : lettres/chiffres/.:_- (ex: qwen3:8b).";
            return false;
        }
        erreur = null;
        return true;
    }

    private enum Niveau { Tous, Rapide, Raisonnement }

    private static Niveau ParseNiveau(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("niveau", out var n)) return Niveau.Tous;
        return n.Trim().ToLowerInvariant() switch
        {
            "rapide" or "fast" => Niveau.Rapide,
            "raisonnement" or "reasoning" or "puissant" => Niveau.Raisonnement,
            _ => Niveau.Tous
        };
    }

    private static string NiveauTexte(Niveau niveau) => niveau switch
    {
        Niveau.Rapide => "rapide",
        Niveau.Raisonnement => "raisonnement",
        _ => "tous"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 Go";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F0} Mo";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} Go";
    }
}
