using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Routines;

namespace JarvisAI.Infrastructure.Tools;

// Gestion des routines (docs/routines.md) : enchaînements déclenchés par la
// présence (retour/départ) ou une heure fixe. Actions = outil + JSON + message.
public sealed class RoutinesTool : ITool
{
    private readonly RoutinesStore _store;
    private readonly RoutineEngine _engine;

    public RoutinesTool(RoutinesStore store, RoutineEngine engine)
    {
        _store = store;
        _engine = engine;
    }

    public string Name => "routines";
    public string Description =>
        "Gère les routines : « liste », « run <nom> », « on <nom> » / « off <nom> ». " +
        "Les routines sont définies dans routines.json (déclencheur présence ou horaire).";
    public string Category => "automation";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium; // run exécute les actions configurées

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "liste | run | on | off", typeof(string), required: true),
        new("nom", "Nom de la routine (run/on/off)", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("nom", out var nom);
        var settings = _store.Get();

        switch (action?.ToLowerInvariant())
        {
            case "liste":
            {
                if (settings.Items.Count == 0)
                    return ToolResult.Succeeded("Aucune routine configurée. Crée-les dans %LOCALAPPDATA%\\JarvisAI\\routines.json.");
                var lignes = settings.Items.Select(r =>
                {
                    var declencheur = r.Declencheur == "horaire" ? $"tous les jours à {r.Horaire}" : r.Declencheur;
                    return $"  • {r.Nom} — {declencheur}, {(r.Active ? "active" : "en pause")}, {r.Actions.Count} action(s)";
                });
                return ToolResult.Succeeded($"{settings.Items.Count} routine(s) :\n" + string.Join("\n", lignes));
            }

            case "run" when !string.IsNullOrWhiteSpace(nom):
                return ToolResult.Succeeded(await _engine.RunByNameAsync(nom, ct));

            case "on" when !string.IsNullOrWhiteSpace(nom):
            case "off" when !string.IsNullOrWhiteSpace(nom):
            {
                var activer = action!.Equals("on", StringComparison.OrdinalIgnoreCase);
                var routine = settings.Items.FirstOrDefault(r =>
                    r.Nom.Equals(nom, StringComparison.OrdinalIgnoreCase));
                if (routine is null)
                    return ToolResult.Failed($"Routine « {nom} » introuvable.");
                routine.Active = activer;
                _store.Save(settings);
                return ToolResult.Succeeded($"Routine « {routine.Nom} » {(activer ? "activée" : "mise en pause")}.");
            }

            default:
                return ToolResult.Failed("Action inconnue. Utilise : liste, run <nom>, on <nom>, off <nom>.");
        }
    }
}