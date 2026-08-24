using System.Diagnostics;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Windows;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// « Suggère un meilleur modèle » / gestion auto : détecte RAM + VRAM,
/// propose des modèles adaptés (LLM, vision, STT) et — après validation
/// vocale de l'utilisateur — les télécharge (ollama pull).
/// </summary>
public sealed class GestionModelesTool : ITool
{
    private sealed record Candidat(string Nom, string Usage, int MinRamGo, int MinVramGo, double GoTelechargement);

    private static readonly Candidat[] Catalogue =
    {
        new("qwen2.5:0.5b",   "llm léger",        4,  0,  0.4),
        new("llama3.2:3b",    "llm équilibré",    8,  0,  2.0),
        new("qwen2.5:7b",     "llm puissant",    10,  6,  4.7),
        new("qwen2.5:14b",    "llm très puissant",16, 10, 9.0),
        new("qwen2.5-coder:7b","code",            10,  6,  4.7),
        new("llava:7b",       "vision",           10,  6,  4.7),
        new("minicpm-v",      "vision fine",      12,  8,  5.5),
    };

    private readonly IVoiceConfirmationChannel _confirmation;
    private readonly Application.Voice.IVoiceSettingsStore _settingsStore;

    public GestionModelesTool(IVoiceConfirmationChannel confirmation, Application.Voice.IVoiceSettingsStore settingsStore)
    {
        _confirmation = confirmation;
        _settingsStore = settingsStore;
    }

    public string Name => "gestion_modeles";
    public string Description =>
        "Gestion automatique des modèles IA : détecte la machine (RAM/VRAM), suggère les meilleurs modèles compatibles " +
        "(LLM, vision…) et les télécharge après accord de l'utilisateur. Actions : detecte, suggere, telecharge.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new[]
    {
        new ToolParameter("action", "detecte | suggere | telecharge", typeof(string), required: true),
        new ToolParameter("usage", "Filtre : llm | vision | code (suggere)", typeof(string), required: false),
        new ToolParameter("nom", "Nom du modèle à télécharger, ex qwen2.5:7b (telecharge)", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            parameters ??= new Dictionary<string, string>();
            var action = parameters.TryGetValue("action", out var a) ? a.Trim().ToLowerInvariant() : "";
            var machine = HardwareProbe.Detecter();

            if (action == "detecte")
            {
                return ToolResult.Succeeded(
                    $"Machine : {machine.RamGo} Go de RAM, GPU {machine.Gpu ?? "aucun détecté"}" +
                    (machine.VramGo > 0 ? $" avec {machine.VramGo} Go de VRAM." : "."));
            }

            if (action == "suggere")
            {
                var filtre = parameters.TryGetValue("usage", out var u) ? u.Trim().ToLowerInvariant() : "";
                var compatibles = Catalogue
                    .Where(c => c.MinRamGo <= machine.RamGo && c.MinVramGo <= machine.VramGo)
                    .Where(c => filtre.Length == 0 || c.Usage.Contains(filtre, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.Nom.Contains(':') ? int.Parse(c.Nom.Split(':')[1].Split('-')[0].TrimEnd('b', 'B')) : 0)
                    .ToList();

                if (compatibles.Count == 0)
                    return ToolResult.Succeeded("Aucun modèle supplémentaire compatible avec cette machine.");

                var dejaInstalles = await ListerOllamaAsync(cancellationToken);
                var suggestions = compatibles
                    .Where(c => !dejaInstalles.Any(d => d.StartsWith(c.Nom.Split(':')[0], StringComparison.Ordinal)))
                    .Select(c => $"• {c.Nom} — {c.Usage}, ~{c.GoTelechargement:0.#} Go");

                return ToolResult.Succeeded(
                    $"Pour {machine.RamGo} Go de RAM" +
                    (machine.VramGo > 0 ? $" et {machine.VramGo} Go de VRAM" : "") +
                    ", je peux installer :\n" + string.Join("\n", suggestions) +
                    "\nDis-moi lequel et je le télécharge après ta confirmation.");
            }

            if (action == "telecharge")
            {
                var nom = parameters.TryGetValue("nom", out var n) ? n.Trim() : "";
                var candidat = Catalogue.FirstOrDefault(c => c.Nom.Equals(nom, StringComparison.OrdinalIgnoreCase));
                if (nom.Length == 0)
                    return ToolResult.Failed("Précise le nom du modèle (ex qwen2.5:7b). Utilise d'abord l'action suggere.");
                if (candidat is not null)
                {
                    if (candidat.MinRamGo > machine.RamGo || candidat.MinVramGo > machine.VramGo)
                        return ToolResult.Failed(
                            $"« {nom} » demande au moins {candidat.MinRamGo} Go de RAM" +
                            (candidat.MinVramGo > 0 ? $" et {candidat.MinVramGo} Go de VRAM" : "") +
                            $" ; cette machine a {machine.RamGo} Go" + (machine.VramGo > 0 ? $" / {machine.VramGo} Go" : "") + ".");
                }

                // Validation obligatoire avant tout téléchargement.
                var taille = candidat?.GoTelechargement ?? 4.0;
                var reponse = await _confirmation.AskAsync(
                    $"Je vais télécharger le modèle « {nom} », environ {taille:0.#} gigaoctets. Je lance ?",
                    TimeSpan.FromSeconds(15), cancellationToken);
                if (reponse is not { Accepted: true })
                    return ToolResult.Succeeded("Téléchargement annulé.");

                var ok = await OllamaPullAsync(nom, cancellationToken);
                return ok
                    ? ToolResult.Succeeded($"Modèle « {nom} » installé avec succès. Il est disponible immédiatement.")
                    : ToolResult.Failed($"Téléchargement de « {nom} » échoué. Vérifie qu'Ollama tourne sur la machine.");
            }

            return ToolResult.Failed("Action inconnue. Utilise detecte, suggere ou telecharge.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur gestion des modèles : {ex.Message}");
        }
    }

    private static async Task<List<string>> ListerOllamaAsync(CancellationToken ct)
    {
        try
        {
            var sortie = await ExecuterAsync("ollama", "list", TimeSpan.FromSeconds(10), ct);
            return sortie.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(l => l.Split(' ')[0]).ToList();
        }
        catch { return new(); }
    }

    private static async Task<bool> OllamaPullAsync(string nom, CancellationToken ct)
    {
        try
        {
            var sortie = await ExecuterAsync("ollama", $"pull {nom}", TimeSpan.FromMinutes(30), ct);
            return !sortie.Contains("error", StringComparison.OrdinalIgnoreCase)
                   && !sortie.Contains("not found", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task<string> ExecuterAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} introuvable");
        var tacheSortie = p.StandardOutput.ReadToEndAsync();
        var tacheErreur = p.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"{exe} {args} : dépassé {timeout.TotalMinutes:0} min");
        }
        return await tacheSortie + await tacheErreur;
    }
}
