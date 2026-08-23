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
    // Résolution paresseuse : vérifie l'existence des outils sans cycle DI.
    private readonly Lazy<IToolRegistry> _registry;

    public RoutinesTool(RoutinesStore store, RoutineEngine engine, Lazy<IToolRegistry> registry)
    {
        _store = store;
        _engine = engine;
        _registry = registry;
    }

    public string Name => "routines";
    public string Description =>
        "Gère les routines : « liste », « run <nom> », « on <nom> » / « off <nom> », " +
        "« ajoute » (nom, declencheur=horaire|presence_retour|presence_depart, horaire=HH:mm, " +
        "outil + args_json + message), « supprime » (nom). Les routines créées ici sont " +
        "exécutées automatiquement par le moteur (présence ou heure fixe).";
    public string Category => "automation";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium; // run exécute les actions configurées

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "liste | run | on | off | ajoute | supprime", typeof(string), required: true),
        new("nom", "Nom de la routine (run/on/off/ajoute/supprime)", typeof(string), required: false),
        new("declencheur", "horaire | presence_retour | presence_depart (ajoute)", typeof(string), required: false),
        new("horaire", "Heure au format HH:mm pour le déclencheur horaire (ajoute)", typeof(string), required: false),
        new("outil", "Nom de l'outil à exécuter, vide si simple message vocal (ajoute)", typeof(string), required: false),
        new("args_json", "Paramètres de l'outil en JSON, ex {\"action\":\"scene\",\"id\":\"nuit\"} (ajoute)", typeof(string), required: false),
        new("message", "Message annoncé à voix haute par la routine (ajoute)", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("nom", out var nom);
        parameters.TryGetValue("declencheur", out var declencheur);
        parameters.TryGetValue("horaire", out var horaire);
        parameters.TryGetValue("outil", out var outil);
        parameters.TryGetValue("args_json", out var argsJson);
        parameters.TryGetValue("message", out var message);
        var settings = _store.Get();

        switch (action?.ToLowerInvariant())
        {
            case "liste":
            {
                if (settings.Items.Count == 0)
                    return ToolResult.Succeeded("Aucune routine configurée. Utilise l'action « ajoute » pour en créer.");
                var lignes = settings.Items.Select(r =>
                {
                    var declencheurTxt = r.Declencheur == "horaire" ? $"tous les jours à {r.Horaire}" : r.Declencheur;
                    return $"  • {r.Nom} — {declencheurTxt}, {(r.Active ? "active" : "en pause")}, {r.Actions.Count} action(s)";
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

            case "ajoute":
            {
                if (string.IsNullOrWhiteSpace(nom))
                    return ToolResult.Failed("Nom requis pour créer une routine.");
                nom = nom.Trim();
                declencheur = (declencheur ?? "").Trim().ToLowerInvariant();
                if (declencheur is not ("horaire" or "presence_retour" or "presence_depart"))
                    return ToolResult.Failed("Déclencheur invalide : horaire | presence_retour | presence_depart.");
                if (declencheur == "horaire")
                {
                    if (!TimeSpan.TryParseExact(horaire?.Trim() ?? "", @"hh\:mm", null, out _))
                        return ToolResult.Failed("Pour un déclencheur horaire, fournis horaire=HH:mm (ex 07:30).");
                }
                if (string.IsNullOrWhiteSpace(outil) && string.IsNullOrWhiteSpace(message))
                    return ToolResult.Failed("Fournis au moins un outil ou un message à annoncer.");
                if (!string.IsNullOrWhiteSpace(outil))
                {
                    // L'outil doit exister : on évite d'enregistrer une routine morte.
                    if (_registry.Value.GetByName(outil.Trim()) is null)
                        return ToolResult.Failed($"Outil « {outil} » inconnu — vérifie le nom.");
                }

                var existante = settings.Items.FirstOrDefault(r => r.Nom.Equals(nom, StringComparison.OrdinalIgnoreCase));
                var nouvelle = new Routine
                {
                    Nom = nom,
                    Declencheur = declencheur,
                    Horaire = horaire?.Trim() ?? "",
                    Active = true,
                    Actions = new List<RoutineAction>
                    {
                        new()
                        {
                            Outil = outil?.Trim() ?? "",
                            ArgsJson = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson,
                            Message = message ?? ""
                        }
                    }
                };
                if (existante is not null)
                    settings.Items[settings.Items.IndexOf(existante)] = nouvelle;
                else
                    settings.Items.Add(nouvelle);
                _store.Save(settings);

                var declencheurTxt = declencheur == "horaire" ? $"tous les jours à {nouvelle.Horaire}" : declencheur;
                return ToolResult.Succeeded($"Routine « {nom} » enregistrée ({declencheurTxt}) et activée.");
            }

            case "supprime" when !string.IsNullOrWhiteSpace(nom):
            {
                var routine = settings.Items.FirstOrDefault(r =>
                    r.Nom.Equals(nom, StringComparison.OrdinalIgnoreCase));
                if (routine is null)
                    return ToolResult.Failed($"Routine « {nom} » introuvable.");
                settings.Items.Remove(routine);
                _store.Save(settings);
                return ToolResult.Succeeded($"Routine « {routine.Nom} » supprimée.");
            }

            default:
                return ToolResult.Failed("Action inconnue. Utilise : liste, run <nom>, on <nom>, off <nom>, ajoute, supprime <nom>.");
        }
    }
}