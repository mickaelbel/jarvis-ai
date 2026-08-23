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

    public static bool Enabled { get; set; } = true;
    public static int IntervalMinutes { get; set; } = 45;

    public GoalRunner(
        ObjectifsStore store,
        Lazy<AIService> ai,
        Lazy<IToolRegistry> registry,
        IToolExecutor executor,
        ILogger<GoalRunner> logger)
    {
        _store = store;
        _ai = ai;
        _registry = registry;
        _executor = executor;
        _logger = logger;
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

    internal async Task<string?> AdvanceOnceAsync(CancellationToken ct)
    {
        var settings = _store.Get();
        var objectif = settings.Items.FirstOrDefault(o => o.Statut == "actif");
        if (objectif is null) return null;

        var plan = await PlanNextActionAsync(objectif, ct);
        if (plan is null)
        {
            AppendJournal(objectif, "⚠ Le planificateur n'a pas produit d'action exploitable.");
        }
        else if (plan.Fini)
        {
            objectif.Statut = "termine";
            AppendJournal(objectif, $"✔ Objectif marqué terminé : {plan.Etape}");
        }
        else
        {
            AppendJournal(objectif, $"▶ {plan.Etape}");
            if (!string.IsNullOrWhiteSpace(plan.Outil))
            {
                var result = await RunToolAsync(plan, objectif.Titre, ct);
                AppendJournal(objectif, $"   ↳ [{plan.Outil}] {Truncate(result, 300)}");
            }
            if (!string.IsNullOrWhiteSpace(plan.Message))
                AppendJournal(objectif, $"   note : {plan.Message}");
        }

        objectif.MisAJour = DateTime.UtcNow;
        TrimJournal(objectif);
        _store.Save(settings);
        return plan is null ? null : plan.Etape;
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
