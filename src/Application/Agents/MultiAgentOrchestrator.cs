using JarvisAI.Application.AI;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Application.Agents;

public sealed record MultiAgentSubGoal(string Tab, string Goal);

public sealed record MultiAgentDecision(bool Parallel, IReadOnlyList<MultiAgentSubGoal> Tasks);

public sealed record MultiAgentResult
{
    public bool Success { get; init; }
    public string FinalResponse { get; init; } = string.Empty;
    public IReadOnlyList<MultiAgentSubGoal> SubGoals { get; init; } = new List<MultiAgentSubGoal>();
    public IReadOnlyList<OrchestrationResult> SubResults { get; init; } = new List<OrchestrationResult>();
    public string? Reason { get; init; }
    public int SubAgents { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Orchestrateur MULTI-AGENT : décompose un objectif en sous-tâches indépendantes,
/// lance chaque sous-tâche sur un agent autonome dédié (chacun pilotant son propre
/// onglet navigateur nommé), en PARALLÈLE, puis synthétise une réponse finale.
/// Ex : « trouve des coques s26 ultra carbone ET aramid » → 2 agents en parallèle.
/// </summary>
public interface IMultiAgentOrchestrator
{
    Task<MultiAgentResult> ExecuteAsync(string goal, ModelSelectionMode mode, CancellationToken cancellationToken = default);
}

public sealed class MultiAgentOrchestrator : IMultiAgentOrchestrator
{
    private const int MaxSubAgents = 4;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(4);

    private readonly IAIProvider _provider;
    private readonly IModelRouter _router;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ILogger<MultiAgentOrchestrator> _logger;

    public MultiAgentOrchestrator(
        IAIProvider provider,
        IModelRouter router,
        IAgentOrchestrator orchestrator,
        ILogger<MultiAgentOrchestrator> logger)
    {
        _provider = provider;
        _router = router;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task<MultiAgentResult> ExecuteAsync(string goal, ModelSelectionMode mode, CancellationToken cancellationToken = default)
    {
        var goalText = goal?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(goalText))
        {
            return new MultiAgentResult { Success = false, Reason = "Empty goal" };
        }

        var started = DateTime.UtcNow;
        var route = _router.Resolve(goalText, conversation: null, mode);
        var model = route.Model;

        // 1) Heuristique pré-LLM : les tâches clairement simples/séquentielles
        //    sautent l'appel « architecte » et partent direct en agent unique
        //    (zéro surcoût). Les tâches à tendance parallèle (variantes/options)
        //    lancent la décomposition par le LLM.
        if (LooksSingular(goalText))
        {
            _logger.LogInformation("[MultiAgent] Tâche simple/séquentielle détectée — agent unique : {Goal}", goalText);
            return await RunSingleAgentAsync(goalText, mode, started, cancellationToken);
        }
        var parallelHint = LooksParallel(goalText);
        if (parallelHint)
            _logger.LogInformation("[MultiAgent] Indices de parallélisme détectés — décomposition : {Goal}", goalText);

        // 2) Le LLM « architecte » décide finement : sous-tâches parallèles ou séquentiel.
        var decision = await DecomposeAsync(goalText, model, parallelHint, cancellationToken);
        if (decision.Parallel && decision.Tasks.Count > 1)
        {
            _logger.LogInformation("[MultiAgent] {N} sous-agents en parallèle : {Tabs}",
                decision.Tasks.Count, string.Join(", ", decision.Tasks.Select(s => s.Tab)));

            // 3) Chaque sous-tâche = un agent autonome indépendant, avec son onglet nommé.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(DefaultTimeout);
            var tasks = decision.Tasks.Select(sub => RunSubAgentAsync(sub, mode, cts.Token)).ToArray();
            var subResults = await Task.WhenAll(tasks);

            // 4) Synthèse des résultats en une réponse finale.
            var synthesis = await SynthesizeAsync(goalText, model, decision.Tasks, subResults, cts.Token);

            // Au moins 2/3 des sous-agents doivent réussir pour considérer le résultat global comme succès.
            var successCount = subResults.Count(r => r.Success);
            var success = successCount >= Math.Max(1, (int)(subResults.Length * 0.67));
            _logger.LogInformation("[MultiAgent] Sous-résultats: {Success}/{Total} succès",
                successCount, subResults.Length);
            return new MultiAgentResult
            {
                Success = success,
                FinalResponse = synthesis,
                SubGoals = decision.Tasks,
                SubResults = subResults,
                SubAgents = subResults.Length,
                Reason = null,
                Duration = DateTime.UtcNow - started
            };
        }

        // 5) Tâche séquentielle (ou ambiguë) : un agent unique, sans onglet nommé inutile.
        _logger.LogInformation("[MultiAgent] Tâche séquentielle — agent unique");
        return await RunSingleAgentAsync(goalText, mode, started, cancellationToken);
    }

    private async Task<MultiAgentResult> RunSingleAgentAsync(string goal, ModelSelectionMode mode, DateTime started, CancellationToken ct)
    {
        var result = await _orchestrator.ExecuteAsync(new AgentRequest
        {
            Goal = goal,
            Mode = mode,
            Source = "multi_agent_single",
            AllowParallelTools = true
        }, ct);
        return new MultiAgentResult
        {
            Success = result.Success,
            FinalResponse = result.FinalResponse,
            SubAgents = 1,
            SubResults = new List<OrchestrationResult> { result },
            Reason = result.Reason,
            Duration = DateTime.UtcNow - started
        };
    }

    private async Task<OrchestrationResult> RunSubAgentAsync(MultiAgentSubGoal sub, ModelSelectionMode mode, CancellationToken ct)
    {
        try
        {
            return await _orchestrator.ExecuteAsync(new AgentRequest
            {
                Goal = sub.Goal,
                Mode = mode,
                Source = "multi_agent",
                Metadata = new Dictionary<string, string> { ["modetab"] = sub.Tab },
                AllowParallelTools = true,
                Timeout = DefaultTimeout
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return new OrchestrationResult { Success = false, FinalResponse = "(sous-agent annulé)", Reason = "Cancelled" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MultiAgent] Sous-agent « {Tab} » échoué", sub.Tab);
            return new OrchestrationResult { Success = false, FinalResponse = $"(sous-agent « {sub.Tab} » en erreur)", Reason = ex.Message };
        }
    }

    // ── Décision de parallélisation ──────────────────────────────────────────
    private async Task<MultiAgentDecision> DecomposeAsync(string goal, string model, bool parallelHint, CancellationToken ct)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Tu es un architecte de tâches. Juge D'ABORD si l'objectif est décomposable en sous-tâches INDÉPENDANTES exécutables EN PARALLÈLE par des agents distincts.");
        prompt.AppendLine("- `parallel = true` UNIQUEMENT s'il y a au moins 2 tâches vraiment indépendantes (variantes/options/candidats: 'carbone' vs 'aramid', 'Google' vs 'Amazon', 'pros' vs 'cons').");
        prompt.AppendLine("- `parallel = false` si la tâche est séquentielle, dépendante, ou simple (ex: une demande unique, une action précise, un calcul).");
        prompt.AppendLine("- Si parallel = true → `tasks` avec au plus 4 entrées. Chaque sous-tâche doit être autonome et parallélisable.");
        prompt.AppendLine("- Si parallel = false → `tasks` vide.");
        prompt.AppendLine("- `tab` = court nom ASCII de l'onglet navigateur (ex: carbone, aramid, site_a, site_b).");
        prompt.AppendLine("- `goal` = instructions complètes et indépendantes pour CET agent.");
        prompt.AppendLine("Réponds UNIQUEMENT en JSON : {\"parallel\": true|false, \"tasks\":[{\"tab\":\"...\",\"goal\":\"...\"}]}");
        if (parallelHint)
            prompt.AppendLine("INDICE : l'objectif évoque plusieurs variantes/options → penche vers parallel=true si tu en identifies au moins 2 indépendantes.");
        prompt.AppendLine();
        prompt.AppendLine($"Objectif : {goal}");

        var request = new AIRequest(
            "Tu produis uniquement du JSON valide.",
            new[] { AIMessage.User(prompt.ToString()) },
            model: model,
            temperature: 0.2f,
            maxTokens: 1500);

        var response = await _provider.ChatAsync(request, ct);
        if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
        {
            _logger.LogWarning("[MultiAgent] Décision sans réponse — séquentiel par défaut");
            return new MultiAgentDecision(false, new List<MultiAgentSubGoal>());
        }

        return ParseDecision(response.Content);
    }

    public static MultiAgentDecision ParseDecision(string content)
    {
        var json = ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
            return new MultiAgentDecision(false, new List<MultiAgentSubGoal>());
        try
        {
            using var doc = JsonDocument.Parse(json);
            var parallel = doc.RootElement.TryGetProperty("parallel", out var pEl) && pEl.ValueKind == JsonValueKind.True;
            var subGoals = parallel ? ParseSubGoalsFromElement(doc.RootElement) : new List<MultiAgentSubGoal>();
            return new MultiAgentDecision(parallel, subGoals);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[MultiAgent] ParseDecision failed: {ex.Message}");
            return new MultiAgentDecision(false, new List<MultiAgentSubGoal>());
        }
    }

    public static List<MultiAgentSubGoal> ParseSubGoals(string content)
    {
        var json = ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json)) return new List<MultiAgentSubGoal>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseSubGoalsFromElement(doc.RootElement);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[MultiAgent] ParseSubGoals failed: {ex.Message}");
            return new List<MultiAgentSubGoal>();
        }
    }

    private static List<MultiAgentSubGoal> ParseSubGoalsFromElement(JsonElement root)
    {
        if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return new List<MultiAgentSubGoal>();

        var list = new List<MultiAgentSubGoal>();
        foreach (var t in tasks.EnumerateArray())
        {
            var tab = (t.TryGetProperty("tab", out var tabEl) ? tabEl.GetString() : null)?.Trim() ?? string.Empty;
            var g = (t.TryGetProperty("goal", out var gEl) ? gEl.GetString() : null)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(g)) continue;
            if (string.IsNullOrWhiteSpace(tab)) tab = "task-" + (list.Count + 1);
            list.Add(new MultiAgentSubGoal(TabName(tab), g));
            if (list.Count >= MaxSubAgents) break;
        }
        return list;
    }

    private static string TabName(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c is '-' or '_') sb.Append(c);
        }
        var name = sb.ToString();
        return string.IsNullOrWhiteSpace(name) ? "task" : (name.Length > 20 ? name[..20] : name);
    }

    // ── Heuristiques pré-LLM (décision rapide sans appel coûteux) ────────────
    private static readonly string[] SingularSignals =
    {
        "calcule", "calcul", "traduis", "traduit", "résume", "resume", "écris", "ecris",
        "rédige", "redige", "explique", "explique-moi", "qu'est-ce que", "c'est quoi",
        "definition", "définition", "orthographe", "synonyme", "combien font", "conjugue",
        "raconte", "décris", "decris", "donne-moi la recette", "convertit", "converti"
    };
    private static readonly string[] ParallelSignals =
    {
        " et ", " ou ", " vs ", " vs. ", " comparer ", " compare ", " ainsi que ", " et aussi ",
        " plusieurs ", " les deux ", " différentes ", " differents ", " variantes ", " options ",
        " candidats ", " en parallèle ", " en parallele ", " parallèlement ", " en même temps ",
        ";", " et faire ", " trouver ... et "
    };

    /// <summary>true si la tâche ressemble à une demande unique/simple → agent unique.</summary>
    public static bool LooksSingular(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return true;
        var g = " " + goal.ToLowerInvariant() + " ";
        return SingularSignals.Any(s => g.Contains(s, StringComparison.Ordinal));
    }

    /// <summary>true si la tâche évoque plusieurs variantes/options → décomposition parallèle.</summary>
    public static bool LooksParallel(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return false;
        var g = " " + goal.ToLowerInvariant() + " ";
        return ParallelSignals.Any(s => g.Contains(s, StringComparison.Ordinal));
    }

    // ── Synthèse ─────────────────────────────────────────────────────────────
    private async Task<string> SynthesizeAsync(
        string goal,
        string model,
        IReadOnlyList<MultiAgentSubGoal> subGoals,
        IReadOnlyList<OrchestrationResult> subResults,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Objectif : {goal}");
        sb.AppendLine();
        for (var i = 0; i < subResults.Count; i++)
        {
            var sub = subGoals.Count > i ? subGoals[i] : subGoals[^1];
            var res = subResults[i];
            sb.AppendLine($"────────── SOUS-TÂCHE « {sub.Tab} » : {sub.Goal} ──────────");
            sb.AppendLine(res.Success ? res.FinalResponse : $"(échec) {res.FinalResponse}");
            sb.AppendLine();
        }

        var prompt = new StringBuilder();
        prompt.AppendLine("Tu es Jarvis. Range et synthétise les résultats des sous-agents ci-dessous en une réponse finale claire et utile à l'utilisateur, sans répéter mécaniquement tout le contenu, en français.");
        prompt.AppendLine("Compare/contraste si pertinent (variantes, prix, options). Signale les échecs éventuels.");
        prompt.AppendLine(sb.ToString());

        try
        {
            var request = new AIRequest(
                "Tu es Jarvis, assistant synthétiseur.",
                new[] { AIMessage.User(prompt.ToString()) },
                model: model,
                temperature: 0.3f,
                maxTokens: 2048);
            var response = await _provider.ChatAsync(request, ct);
            return response.Success && !string.IsNullOrWhiteSpace(response.Content) ? response.Content.Trim() : sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MultiAgent] Synthèse échouée — retour brut");
            return sb.ToString();
        }
    }

    private static string ExtractJson(string content)
    {
        var text = content.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return string.Empty;
        var candidate = text.Substring(start, end - start + 1);
        // Nettoie les fences markdown éventuelles.
        candidate = candidate.Replace("```json", "").Replace("```", "").Trim();
        return candidate;
    }
}
