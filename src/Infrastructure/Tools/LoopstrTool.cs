using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class LoopstrTool : ITool
{
    private readonly IntegrationsStore _store;
    private readonly ILogger<LoopstrTool> _logger;
    private readonly HttpClient _http;

    public LoopstrTool(IntegrationsStore store, ILogger<LoopstrTool> logger)
    {
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "loopstr";
    public string Description =>
        "Récupère les deadlines et absences depuis les URLs iCal abonnées (config Google.IcalUrls). " +
        "Action : deadlines (période par défaut 7 jours). Cache 15 min.";
    public string Category => "calendar";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "deadlines", typeof(string), required: true),
        new("days", "Nombre de jours (défaut 7)", typeof(string))
    };

    private DateTime? _cacheTime;
    private List<IcalEvent>? _cachedEvents;

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        if (action?.ToLowerInvariant() != "deadlines")
            return ToolResult.Failed("Action inconnue. Valide : deadlines");

        var days = int.TryParse(parameters.GetValueOrDefault("days"), out var d) ? d : 7;
        var icsUrls = _store.Get().Google.IcalUrls;
        if (icsUrls.Count == 0)
            return ToolResult.Failed("Aucune URL iCal configurée. Ajoute des URLs dans Google.IcalUrls des paramètres.");

        try
        {
            var now = DateTime.UtcNow;
            if (_cachedEvents != null && _cacheTime.HasValue && (now - _cacheTime.Value).TotalMinutes < 15)
            {
                return FormatDeadlines(_cachedEvents, days);
            }

            var allEvents = new List<IcalEvent>();
            foreach (var url in icsUrls)
            {
                try
                {
                    using var resp = await _http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) continue;
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    allEvents.AddRange(ParseIcal(text));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Loopstr] Échec fetch {Url}", url);
                }
            }

            _cachedEvents = allEvents;
            _cacheTime = now;
            return FormatDeadlines(allEvents, days);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Loopstr] deadlines échoué");
            return ToolResult.Failed($"Erreur iCal : {ex.Message}");
        }
    }

    private static ToolResult FormatDeadlines(List<IcalEvent> events, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(days);
        var deadlines = events.Where(e => e.IsDeadline && e.Start <= cutoff && e.Start >= DateTime.UtcNow)
            .OrderBy(e => e.Start).ToList();
        var absences = events.Where(e => e.IsAbsence && e.Start <= cutoff && e.End >= DateTime.UtcNow)
            .OrderBy(e => e.Start).ToList();

        if (deadlines.Count == 0 && absences.Count == 0)
            return ToolResult.Succeeded($"Aucune deadline/absence dans les {days} prochains jours.");

        var sb = new System.Text.StringBuilder($"ICALENDAR ({deadlines.Count} deadlines, {absences.Count} absences sur {days}j) :\n");
        foreach (var e in deadlines.Take(15))
            sb.AppendLine($"  📅 {e.Start:dd/MM HH:mm} — {e.Summary} ({e.SourceUrl})");
        foreach (var e in absences.Take(10))
            sb.AppendLine($"  🏖 {e.Start:dd/MM} → {e.End:dd/MM} — {e.Summary} ({e.SourceUrl})");
        return ToolResult.Succeeded(sb.ToString());
    }

    private static List<IcalEvent> ParseIcal(string text)
    {
        var events = new List<IcalEvent>();
        var lines = text.Split('\n');
        IcalEvent? current = null;
        string? currentUrl = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("BEGIN:VEVENT"))
            {
                current = new IcalEvent();
            }
            else if (line.StartsWith("END:VEVENT") && current != null)
            {
                current.SourceUrl = currentUrl ?? "";
                ClassifyEvent(current);
                events.Add(current);
                current = null;
            }
            else if (current != null)
            {
                if (line.StartsWith("DTSTART")) current.Start = ParseIcalDate(line);
                else if (line.StartsWith("DTEND")) current.End = ParseIcalDate(line);
                else if (line.StartsWith("SUMMARY")) current.Summary = line.Substring(8);
                else if (line.StartsWith("DESCRIPTION")) current.Description = line.Substring(12);
                else if (line.StartsWith("UID")) current.Uid = line.Substring(4);
            }
            else if (line.StartsWith("X-WR-CALNAME") || line.StartsWith("X-WR-CALDESC"))
            {
                currentUrl = line.Split(':', 2).LastOrDefault()?.Trim();
            }
        }
        return events;
    }

    private static DateTime? ParseIcalDate(string line)
    {
        var val = line.Split(':', 2).LastOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(val)) return null;
        // Handle DATE (YYYYMMDD) and DATE-TIME (YYYYMMDDTHHMMSSZ or with TZ)
        if (val.Length == 8 && DateTime.TryParseExact(val, "yyyyMMdd", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var d1))
            return DateTime.SpecifyKind(d1, DateTimeKind.Utc);
        if (val.EndsWith("Z") && DateTime.TryParseExact(val, "yyyyMMddTHHmmssZ", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var d2))
            return d2;
        if (DateTime.TryParse(val, out var d3)) return d3.ToUniversalTime();
        return null;
    }

    private static void ClassifyEvent(IcalEvent e)
    {
        var hay = $"{e.Summary} {e.Description}".ToLowerInvariant();
        var absenceKws = new[] { "vacance", "vacances", "congé", "conge", "conges", "off", "holiday", "absent", "indisponible", "dispo", "unavailable", "ooo", "fermé", "ferme" };
        e.IsAbsence = absenceKws.Any(k => hay.Contains(k));
        e.IsDeadline = !e.IsAbsence && (hay.Contains("deadline") || hay.Contains("échéance") || hay.Contains("echeance") || hay.Contains("remise") || hay.Contains("rendu") || hay.Contains("dû") || hay.Contains("du "));
    }

    private sealed class IcalEvent
    {
        public string Uid { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public string SourceUrl { get; set; } = "";
        public bool IsDeadline { get; set; }
        public bool IsAbsence { get; set; }
    }
}