using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Goals;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Outil « objectifs » : objectifs long terme persistants que le GoalRunner
/// fait avancer périodiquement (une action par cycle, via les outils existants).
/// </summary>
public sealed class ObjectifsTool : ITool
{
    private readonly ObjectifsStore _store;

    public ObjectifsTool(ObjectifsStore store) => _store = store;

    public string Name => "objectifs";
    public string Description =>
        "Gère les objectifs long terme de Jarvis : « liste », " +
        "« ajoute » (titre, details), « statut » (titre), « termine » (titre), " +
        "« abandonne » (titre), « supprime » (titre). Un objectif actif est avancé " +
        "automatiquement, une action à la fois, toutes les ~45 minutes.";
    public string Category => "automation";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "liste | ajoute | statut | termine | abandonne | supprime", typeof(string), required: true),
        new("titre", "Titre de l'objectif", typeof(string), required: false),
        new("details", "Précisions sur le résultat attendu (ajoute)", typeof(string), required: false)
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("titre", out var titre);
        parameters.TryGetValue("details", out var details);

        var settings = _store.Get();
        action = action?.ToLowerInvariant() ?? "";

        switch (action)
        {
            case "liste":
            {
                if (settings.Items.Count == 0)
                    return Task.FromResult(ToolResult.Succeeded("Aucun objectif enregistré."));
                var lines = settings.Items.Select(o =>
                    $"- [{o.Statut}] {o.Titre} — MAJ {o.MisAJour.ToLocalTime():dd/MM HH:mm}, {o.Journal.Count} entrée(s) au journal");
                return Task.FromResult(ToolResult.Succeeded(string.Join("\n", lines)));
            }
            case "ajoute":
            {
                if (string.IsNullOrWhiteSpace(titre))
                    return Task.FromResult(ToolResult.Failed("Titre requis."));
                var actifs = settings.Items.Count(o => o.Statut == "actif");
                if (actifs >= 3 && settings.Items.All(o => !o.Titre.Equals(titre!.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return Task.FromResult(ToolResult.Failed(
                        "3 objectifs actifs maximum (pour rester concentré). Termine ou abandonne-en un d'abord."));
                var existant = settings.Items.FirstOrDefault(o => o.Titre.Equals(titre!.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existant is not null)
                {
                    existant.Statut = "actif";
                    existant.Details = string.IsNullOrWhiteSpace(details) ? existant.Details : details;
                    existant.MisAJour = DateTime.UtcNow;
                }
                else
                {
                    settings.Items.Add(new Objectif { Titre = titre.Trim(), Details = details ?? "" });
                }
                _store.Save(settings);
                return Task.FromResult(ToolResult.Succeeded($"Objectif « {titre.Trim()} » actif : il sera avancé automatiquement."));
            }
            case "statut":
            {
                var o = Find(settings, titre);
                if (o is null) return Task.FromResult(ToolResult.Failed($"Objectif « {titre} » introuvable."));
                var journal = string.Join("\n", o.Journal.TakeLast(10));
                return Task.FromResult(ToolResult.Succeeded($"[{o.Statut}] {o.Titre}\n{o.Details}\n\nJournal récent :\n{(journal.Length > 0 ? journal : "(vide)")}".Trim()));
            }
            case "termine":
            {
                var o = Find(settings, titre);
                if (o is null) return Task.FromResult(ToolResult.Failed($"Objectif « {titre} » introuvable."));
                o.Statut = "termine";
                o.MisAJour = DateTime.UtcNow;
                _store.Save(settings);
                return Task.FromResult(ToolResult.Succeeded($"Objectif « {o.Titre} » terminé. Bravo."));
            }
            case "abandonne":
            {
                var o = Find(settings, titre);
                if (o is null) return Task.FromResult(ToolResult.Failed($"Objectif « {titre} » introuvable."));
                o.Statut = "abandonne";
                o.MisAJour = DateTime.UtcNow;
                _store.Save(settings);
                return Task.FromResult(ToolResult.Succeeded($"Objectif « {o.Titre} » abandonné."));
            }
            case "supprime":
            {
                var o = Find(settings, titre);
                if (o is null) return Task.FromResult(ToolResult.Failed($"Objectif « {titre} » introuvable."));
                settings.Items.Remove(o);
                _store.Save(settings);
                return Task.FromResult(ToolResult.Succeeded($"Objectif « {o.Titre} » supprimé."));
            }
            default:
                return Task.FromResult(ToolResult.Failed("Action inconnue : liste | ajoute | statut | termine | abandonne | supprime"));
        }
    }

    private static Objectif? Find(ObjectifsSettings settings, string? titre)
        => settings.Items.FirstOrDefault(o => o.Titre.Equals(titre?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
}
