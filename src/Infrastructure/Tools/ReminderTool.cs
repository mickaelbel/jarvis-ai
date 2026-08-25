using JarvisAI.Application.Agents;
using JarvisAI.Application.Services;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Outil <c>rappel</c> : crée un rappel vocal avec date/heure. Jarvis te le
/// dira à voix haute au moment voulu. Stockage persistant dans
/// %LOCALAPPDATA%\JarvisAI\reminders.json.
/// </summary>
public sealed class ReminderTool : ITool
{
    private readonly IReminderService _service;
    public string Name => "rappel";
    public string Description => "Crée, liste ou supprime des rappels vocaux. Jarvis te rappellera à voix haute à l'heure prévue. Actions : ajouter, lister, supprimer.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public bool McpExpose => true;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "ajouter | lister | supprimer", typeof(string), required: true),
        new ToolParameter("titre", "Titre court du rappel (ajouter)", typeof(string)),
        new ToolParameter("quand", "Date/heure au format ISO : 2026-08-25T15:00 ou 'dans 2 heures' (ajouter). Convertis en UTC.", typeof(string)),
        new ToolParameter("message", "Message à dire à voix haute (optionnel)", typeof(string)),
        new ToolParameter("id", "ID du rappel (supprimer)", typeof(string))
    };

    public ReminderTool(IReminderService service) => _service = service;

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var action = parameters.TryGetValue("action", out var a) ? a.Trim().ToLowerInvariant() : "";
        return action switch
        {
            "ajouter" => Ajouter(parameters),
            "lister" => Lister(),
            "supprimer" => Supprimer(parameters),
            _ => Task.FromResult(ToolResult.Failed("Action inconnue : ajouter, lister, supprimer."))
        };
    }

    private Task<ToolResult> Ajouter(IReadOnlyDictionary<string, string> p)
    {
        if (!p.TryGetValue("titre", out var titre) || string.IsNullOrWhiteSpace(titre))
            return Task.FromResult(ToolResult.Failed("Titre manquant. Donne un titre court pour ton rappel."));

        if (!p.TryGetValue("quand", out var quand) || string.IsNullOrWhiteSpace(quand))
            return Task.FromResult(ToolResult.Failed("Date/heure manquante. Ex : 2026-08-25T15:00 ou « dans 2 heures »."));

        // Conversion date souple : ISO directe, sinon on demande au LLM de reformater.
        DateTime dueUtc;
        if (DateTime.TryParse(quand, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
            dueUtc = parsed.ToUniversalTime();
        else
            return Task.FromResult(ToolResult.Failed(
                $"Impossible de parser « {quand} ». Format attendu : 2026-08-25T15:00 (heure locale). " +
                "Si c'est une expression comme « dans 2 heures », convertis-la en heure ISO d'abord."));

        if (dueUtc <= DateTime.UtcNow)
            return Task.FromResult(ToolResult.Failed("La date/heure doit être dans le futur."));

        p.TryGetValue("message", out var message);
        var reminder = _service.Add(titre, dueUtc, message);
        var localTime = dueUtc.ToLocalTime().ToString("dddd d MMMM yyyy à HH:mm");
        return Task.FromResult(ToolResult.Succeeded(
            $"Rappel « {reminder.Title} » créé pour le {localTime} (ID : {reminder.Id}). " +
            "Je te le rappellerai à voix haute."));
    }

    private Task<ToolResult> Lister()
    {
        var all = _service.GetAll();
        if (all.Count == 0)
            return Task.FromResult(ToolResult.Succeeded("Aucun rappel en cours."));

        var lines = all.Select(r =>
        {
            var status = r.Fired ? "✓ fait" : $"prévu {r.DueUtc.ToLocalTime():dd/MM HH:mm}";
            return $"- [{r.Id}] {r.Title} — {status}";
        });
        return Task.FromResult(ToolResult.Succeeded("Rappels :\n" + string.Join("\n", lines)));
    }

    private Task<ToolResult> Supprimer(IReadOnlyDictionary<string, string> p)
    {
        if (!p.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ToolResult.Failed("ID manquant. Utilise lister pour voir les IDs."));

        return Task.FromResult(_service.Remove(id)
            ? ToolResult.Succeeded($"Rappel {id} supprimé.")
            : ToolResult.Failed($"Rappel {id} introuvable."));
    }
}
