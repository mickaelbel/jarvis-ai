using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Reminders;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Reminders;

/// <summary>
/// Outil "set_reminder" : programme un rappel qui sera annoncé vocalement à
/// l'échéance. Paramètres : text (que rappeler), when ("10 minutes", "dans 2
/// heures", "18:30", "2026-08-08T18:00", "demain 9h"...).
/// </summary>
public sealed class RemindersTool : ITool
{
    private readonly IReminderService _reminderService;

    public string Name => "set_reminder";
    public string Description => "Planifie un rappel qui sera annoncé vocalement à l'échéance. Quand le rappel est demandé, indique le texte à rappeler et le délai (ex: \"dans 10 minutes\", \"2 heures\", \"18:30\", \"demain 9h\").";
    public string Category => "productivity";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("text", "Contenu du rappel", typeof(string), required: true),
        new ToolParameter("when", "Moment du rappel : délai relatif (\"10 minutes\", \"2 heures\") ou horaire (\"18:30\", \"demain 9h\", ISO)", typeof(string), required: true),
    };

    public RemindersTool(IReminderService reminderService)
    {
        _reminderService = reminderService;
    }

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        if (!parameters.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
            return Task.FromResult(ToolResult.Failed("Paramètre 'text' manquant : contenu du rappel."));

        if (!parameters.TryGetValue("when", out var when) || string.IsNullOrWhiteSpace(when))
            return Task.FromResult(ToolResult.Failed("Paramètre 'when' manquant : délai ou horaire du rappel."));

        var dueAt = ParseWhen(when);
        if (dueAt is null)
        {
            return Task.FromResult(ToolResult.Failed(
                "Format de délai non reconnu. Utilisez un relatif (\"10 minutes\", \"2 heures\"), un horaire (\"18:30\", \"demain 9h\") ou une date ISO (\"2026-08-08T18:00\")."));
        }

        if (dueAt <= DateTime.Now)
        {
            return Task.FromResult(ToolResult.Failed($"La date demandée ({dueAt:g}) est déjà passée. Indiquez un délai dans le futur."));
        }

        var reminder = _reminderService.Add(text, dueAt.Value);
        if (string.IsNullOrWhiteSpace(reminder.Id))
            return Task.FromResult(ToolResult.Failed("Impossible d'enregistrer le rappel."));

        var result = new
        {
            Id = reminder.Id,
            Text = reminder.Text,
            DueAt = reminder.DueAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            DueIn = (int)Math.Round((reminder.DueAt - DateTime.Now).TotalMinutes)
        };

        return Task.FromResult(ToolResult.Succeeded(
            $"Rappel programmé pour {result.DueAt} (dans {result.DueIn} min) : {result.Text}"));
    }

    public static DateTime? ParseWhen(string when)
    {
        var input = when.Trim().ToLowerInvariant();

        var relative = Regex.Match(input, @"(?:dans\s+)?(\d+(?:[.,]\d+)?)\s*(minute|minutes|min|heure|heures|h|heure et demie|jour|jours|j|seconde|secondes|s)");
        if (relative.Success)
        {
            if (!double.TryParse(relative.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                return null;

            var unit = relative.Groups[2].Value;
            return unit switch
            {
                "s" or "seconde" or "secondes" => DateTime.Now.AddSeconds(amount),
                "min" or "minute" or "minutes" => DateTime.Now.AddMinutes(amount),
                "h" or "heure" or "heures" => DateTime.Now.AddHours(amount),
                "heure et demie" => DateTime.Now.AddMinutes(90),
                "j" or "jour" or "jours" => DateTime.Now.AddDays(amount),
                _ => null
            };
        }

        // "demain 9h" / "demain 9h30"
        var tomorrow = Regex.Match(input, @"demain\s*(\d{1,2})h(?:(\d{2}))?");
        if (tomorrow.Success)
        {
            var hh = int.Parse(tomorrow.Groups[1].Value);
            var mm = tomorrow.Groups[2].Success ? int.Parse(tomorrow.Groups[2].Value) : 0;
            if (hh is >= 0 and <= 23 && mm is >= 0 and <= 59)
            {
                var baseTime = DateTime.Now.Date.AddDays(1);
                return new DateTime(baseTime.Year, baseTime.Month, baseTime.Day, hh, mm, 0);
            }
        }

        // "18:30" (aujourd'hui) ou "9h" / "9h30" (aujourd'hui)
        var time = Regex.Match(input, @"(\d{1,2})[h:](\d{2})?");
        if (time.Success)
        {
            var hh = int.Parse(time.Groups[1].Value);
            var mm = time.Groups[2].Success ? int.Parse(time.Groups[2].Value) : 0;
            if (hh is >= 0 and <= 23 && mm is >= 0 and <= 59)
            {
                var today = DateTime.Now.Date;
                return new DateTime(today.Year, today.Month, today.Day, hh, mm, 0);
            }
        }

        // ISO "yyyy-MM-dd HH:mm" / "yyyy-MM-ddTHH:mm"
        if (DateTime.TryParse(input, CultureInfo.CurrentCulture, DateTimeStyles.None, out var iso)
            || DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out iso))
        {
            return iso;
        }

        return null;
    }
}
