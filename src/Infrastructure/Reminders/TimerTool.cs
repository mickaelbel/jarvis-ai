using System.Globalization;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Reminders;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Reminders;

/// <summary>
/// Outil "timer" : lance un compte à rebours qui sera annoncé vocalement à la
/// fin. Réutilise le service de rappels (annonce proactive à l'échéance).
/// </summary>
public sealed class TimerTool : ITool
{
    private readonly IReminderService _reminderService;

    public TimerTool(IReminderService reminderService)
    {
        _reminderService = reminderService;
    }

    public string Name => "timer";
    public string Description =>
        "Starts a countdown timer that will be announced aloud when it finishes. " +
        "Parameters: duration (e.g. \"5 minutes\", \"30 seconds\", \"1 heure 15 minutes\") and optionally label " +
        "(what the timer is for). Use it for cooking, short breaks, tasks with a deadline.";
    public string Category => "productivity";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("duration", "Durée du minuteur (\"5 minutes\", \"30 secondes\", \"1 heure\")", typeof(string), required: true),
        new ToolParameter("label", "Raison du minuteur (annoncée à la fin)", typeof(string), required: false, defaultValue: "Le minuteur est terminé")
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        if (!parameters.TryGetValue("duration", out var duration) || string.IsNullOrWhiteSpace(duration))
            return Task.FromResult(ToolResult.Failed("Paramètre 'duration' manquant."));

        var label = parameters.TryGetValue("label", out var lbl) && !string.IsNullOrWhiteSpace(lbl)
            ? lbl.Trim()
            : "Le minuteur est terminé";

        var dueAt = ParseDuration(duration);
        if (dueAt is null)
        {
            return Task.FromResult(ToolResult.Failed(
                "Durée non reconnue. Utilisez par ex. \"5 minutes\", \"30 secondes\", \"1 heure\", \"90 secondes\"."));
        }

        if (dueAt <= DateTime.Now)
        {
            return Task.FromResult(ToolResult.Failed("La durée indiquée est nulle ou déjà écoulée."));
        }

        var totalSeconds = (int)Math.Round((dueAt.Value - DateTime.Now).TotalSeconds);
        var reminder = _reminderService.Add(label, dueAt.Value);
        if (string.IsNullOrWhiteSpace(reminder.Id))
            return Task.FromResult(ToolResult.Failed("Impossible de démarrer le minuteur."));

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        var display = minutes > 0 ? $"{minutes} min" + (seconds > 0 ? $" {seconds} s" : "") : $"{seconds} s";

        return Task.FromResult(ToolResult.Succeeded(
            $"Minuteur lancé : {display} ({reminder.DueAt:HH:mm:ss}). Je vous préviendrai à la fin."));
    }

    private static DateTime? ParseDuration(string input)
    {
        var text = input.Trim().ToLowerInvariant();
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?:dans\s+)?(?:(\d+(?:[.,]\d+)?)\s*(?:seconde|secondes|s)\s*)?(?:(\d+(?:[.,]\d+)?)\s*(?:minute|minutes|min)\s*)?(?:(\d+(?:[.,]\d+)?)\s*(?:heure|heures|h|hres)\s*)?(?:(\d+(?:[.,]\d+)?)\s*(?:jour|jours|j)\s*)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var seconds = GetAmount(match, 1) ?? 0;
        var minutes = GetAmount(match, 2) ?? 0;
        var hours = GetAmount(match, 3) ?? 0;
        var days = GetAmount(match, 4) ?? 0;

        var total = seconds + minutes * 60 + hours * 3600 + days * 86400;
        return total > 0 ? DateTime.Now.AddSeconds(total) : null;
    }

    private static double? GetAmount(System.Text.RegularExpressions.Match match, int group)
    {
        if (!match.Groups[group].Success) return null;
        return double.TryParse(match.Groups[group].Value.Replace(',', '.'), NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }
}
