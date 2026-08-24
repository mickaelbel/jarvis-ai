using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Goals;

/// <summary>
/// Objectifs longue durée : un objectif persistant est avancé
/// périodiquement, une action à la fois. Le LLM propose la prochaine action
/// concrète (éventuellement un outil à exécuter) ; l'exécution passe par le
/// ToolExecutor (donc par les mêmes règles de sécurité que le chat) et le
/// résultat est consigné dans le journal de l'objectif.
/// </summary>
public sealed class GoalRunner : BackgroundService
{
    private readonly ObjectifsStore _store;
    private readonly Lazy<AIService> _ai;
    private readonly Lazy<IToolRegistry> _registry;
    private readonly IToolExecutor _executor;
    private readonly ILogger<GoalRunner> _logger;
    // Optionnels : questions/rappels vocaux si le canal est disponible.
    private readonly Lazy<JarvisAI.Application.Voice.IVoiceConfirmationChannel>? _confirmation;
    private readonly Lazy<JarvisAI.Application.Voice.VoiceConversationService>? _voice;

    public static bool Enabled { get; set; } = true;
    public static int IntervalMinutes { get; set; } = 45;

    public GoalRunner(
        ObjectifsStore store,
        Lazy<AIService> ai,
        Lazy<IToolRegistry> registry,
        IToolExecutor executor,
        ILogger<GoalRunner> logger,
        Lazy<JarvisAI.Application.Voice.IVoiceConfirmationChannel>? confirmation = null,
        Lazy<JarvisAI.Application.Voice.VoiceConversationService>? voice = null)
    {
        _store = store;
        _ai = ai;
        _registry = registry;
        _executor = executor;
        _logger = logger;
        _confirmation = confirmation;
        _voice = voice;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisse le serveur démarrer (voix, intégrations…) avant la première passe.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled) await AdvanceOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Objectifs] cycle échoué");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(10, IntervalMinutes)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Appel externe : avance tous les objectifs actifs maintenant.</summary>
    public Task<string?> RunCycleNowAsync(CancellationToken ct) => AdvanceOnceAsync(ct);

    // Chaîne d'agents : chaque objectif actif avance dans son propre agent
    // (LLM + outils), jusqu'à 3 en parallèle ; le store est sérialisé.
    private const int MaxAgentsParalleles = 3;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Dictionary<string, int> _echecsParObjectif = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<string?> AdvanceOnceAsync(CancellationToken ct)
    {
        var actifs = _store.Get().Items.Where(o => o.Statut == "actif")
            .OrderBy(o => o.MisAJour)
            .Take(MaxAgentsParalleles)
            .ToList();
        if (actifs.Count == 0) return null;

        var resultats = await Task.WhenAll(actifs.Select(o => AdvanceOneAsync(o, ct)));
        return resultats.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
    }

    internal async Task<string?> AdvanceOneAsync(Objectif objectif, CancellationToken ct)
    {
        var settings = _store.Get();
        var plan = await PlanNextActionAsync(objectif, ct);

        if (plan is null)
        {
            _echecsParObjectif.TryGetValue(objectif.Titre, out var echecs);
            echecs++;
            _echecsParObjectif[objectif.Titre] = echecs;
            AppendJournal(objectif, "⚠ Le planificateur n'a pas produit d'action exploitable.");
            if (echecs >= 2 && !objectif.Journal.Any(l => l.Contains("[bloqué]")))
            {
                AppendJournal(objectif, "[bloqué] deux plans infructueux d'affilée — demande d'aide envoyée.");
                await AskUserHelpAsync(objectif, ct);
            }
        }
        else
        {
            _echecsParObjectif[objectif.Titre] = 0;
            if (plan.Fini)
            {
                objectif.Statut = "termine";
                AppendJournal(objectif, $"✔ Objectif marqué terminé : {plan.Etape}");
                await AnnounceCompletionAsync(objectif, ct);
            }
            else
            {
                AppendJournal(objectif, $"▶ {plan.Etape}");
                if (!string.IsNullOrWhiteSpace(plan.Outil))
                {
                    var result = await RunToolAsync(plan, objectif.Titre, ct);
                    AppendJournal(objectif, $"   ↳ [{plan.Outil}] {Truncate(result, 300)}");
                    // Refus utilisateur / blocage sécurité : on demande quoi faire.
                    if (result.Contains("denied", StringComparison.OrdinalIgnoreCase)
                        || result.Contains("blocked", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendJournal(objectif, "[bloqué] action refusée par la sécurité — demande d'arbitrage.");
                        await AskUserHelpAsync(objectif, ct);
                    }
                }
                if (!string.IsNullOrWhiteSpace(plan.Message))
                    AppendJournal(objectif, $"   note : {plan.Message}");
            }
        }

        objectif.MisAJour = DateTime.UtcNow;
        TrimJournal(objectif);
        await _saveLock.WaitAsync(ct);
        try { _store.Save(settings); }
        finally { _saveLock.Release(); }
        return plan is null ? null : plan.Etape;
    }

    /// <summary>Objectif en difficulté : Jarvis pose la question à voix haute.</summary>
    private async Task AskUserHelpAsync(Objectif objectif, CancellationToken ct)
    {
        try
        {
            if (_confirmation is not null && _confirmation.Value.IsSupported)
            {
                var reponse = await _confirmation.Value.AskAsync(
                    $"Je bloque sur l'objectif « {objectif.Titre} ». Veux-tu que je change d'approche ?",
                    TimeSpan.FromSeconds(12), ct);
                AppendJournal(objectif, reponse is { Accepted: true }
                    ? "→ l'utilisateur dit de persévérer autrement."
                    : "→ pas de réponse, nouvelle tentative au prochain cycle.");
            }
            else
            {
                AppendJournal(objectif, "→ canal vocal indisponible pour demander de l'aide.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Objectifs] demande d'aide impossible");
        }
    }

    /// <summary>Rapport final parlé quand un objectif se termine.</summary>
    private async Task AnnounceCompletionAsync(Objectif objectif, CancellationToken ct)
    {
        try
        {
            if (_voice is null) return;
            var dernieres = string.Join(", ", objectif.Journal.TakeLast(3)
                .Select(l => l.Split(']') is { Length: > 1 } parts ? parts[1].Trim() : l));
            await _voice.Value.SpeakAsync(
                $"Objectif « {objectif.Titre} » terminé. Résumé des dernières actions : {Truncate(dernieres, 220)}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Objectifs] annonce finale impossible");
        }
    }

    private async Task<ObjectifPlan?> PlanNextActionAsync(Objectif objectif, CancellationToken ct)
    {
        var outils = string.Join("\n", _registry.Value.GetAll()
            .Select(t => $"- {t.Name} : {t.Description}"));
        if (outils.Length > 6000) outils = outils[..6000];

        var journal = string.Join("\n", objectif.Journal.TakeLast(12));
        var prompt = new StringBuilder()
            .AppendLine("Tu es Jarvis. Un objectif long terme est en cours et tu dois avancer d'UNE seule action concrète maintenant.")
            .AppendLine($"Objectif : {objectif.Titre}")
            .AppendLine(string.IsNullOrWhiteSpace(objectif.Details) ? "" : $"Détails : {objectif.Details}")
            .AppendLine("Actions déjà tentées (du plus ancien au plus récent) :")
            .AppendLine(string.IsNullOrEmpty(journal) ? "(aucune)" : journal)
            .AppendLine()
            .AppendLine("Outils disponibles (extraits) :")
            .AppendLine(outils)
            .AppendLine()
            .AppendLine("Réponds STRICTEMENT avec ce JSON, sans texte autour :")
            .AppendLine(@"{""fini"": false, ""etape"": ""description courte de l'action tentée"", ""outil"": ""nom_d_outil ou chaîne vide"", ""arguments"": {""param"": ""valeur""}, ""message"": ""note libre pour l'utilisateur ou chaîne vide""}")
            .AppendLine(@"Si l'objectif est atteint au vu du journal, mets ""fini"": true.")
            .ToString();

        var response = await _ai.Value.ChatAsync(prompt, null, null, ct);
        if (!response.Success || string.IsNullOrWhiteSpace(response.Content)) return null;

        return ParsePlan(response.Content);
    }

    internal static ObjectifPlan? ParsePlan(string raw)
    {
        var text = raw.Trim();
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var plan = new ObjectifPlan
            {
                Fini = root.TryGetProperty("fini", out var fini) && fini.ValueKind == JsonValueKind.True,
                Etape = root.TryGetProperty("etape", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "",
                Outil = root.TryGetProperty("outil", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "",
                Message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : ""
            };
            if (root.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in args.EnumerateObject())
                    plan.Arguments[p.Name] = p.Value.ToString() ?? "";
            }
            return string.IsNullOrWhiteSpace(plan.Etape) && !plan.Fini ? null : plan;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> RunToolAsync(ObjectifPlan plan, string titre, CancellationToken ct)
    {
        try
        {
            var tool = _registry.Value.GetByName(plan.Outil!);
            if (tool is null) return $"outil inconnu : {plan.Outil}";

            var context = new AgentContext($"[objectif] {titre}", "objectifs",
                new Dictionary<string, object> { ["arguments"] = plan.Arguments });
            var result = await _executor.ExecuteAsync(plan.Outil!, context, ct);
            return result.Success ? result.Output : (result.ErrorMessage ?? "échec");
        }
        catch (Exception ex)
        {
            return "erreur : " + ex.Message;
        }
    }

    private static void AppendJournal(Objectif objectif, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        objectif.Journal.Add($"[{DateTime.Now:dd/MM HH:mm}] {line}");
    }

    private static void TrimJournal(Objectif objectif)
    {
        if (objectif.Journal.Count > 60)
            objectif.Journal = objectif.Journal[^60..].ToList();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}

public sealed class ObjectifPlan
{
    public bool Fini { get; set; }
    public string Etape { get; set; } = "";
    public string Outil { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
