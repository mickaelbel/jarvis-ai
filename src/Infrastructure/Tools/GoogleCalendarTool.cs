using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class GoogleCalendarTool : ITool
{
    private readonly GoogleAuthHelper _auth;
    private readonly IntegrationsStore _store;
    private readonly ILogger<GoogleCalendarTool> _logger;
    private readonly HttpClient _http;

    public GoogleCalendarTool(GoogleAuthHelper auth, IntegrationsStore store, ILogger<GoogleCalendarTool> logger)
    {
        _auth = auth;
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "agenda";
    public string Description =>
        "Google Agenda : liste les événements (tous agendas + iCal abonnés), crée/supprime sur l'agenda principal. " +
        "Actions : list, today, create, delete. Suppression = confirmation N3.";
    public string Category => "calendar";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public string? WaitingPhrase => "Je consulte ton agenda…";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "list | today | create | delete", typeof(string), required: true),
        new("calendar", "Agenda spécifique (id) — défaut = tous", typeof(string)),
        new("start", "Début période ISO (ex: 2025-01-15T00:00:00)", typeof(string)),
        new("end", "Fin période ISO", typeof(string)),
        new("summary", "Titre (create)", typeof(string)),
        new("description", "Description (create)", typeof(string)),
        new("start_time", "Début événement ISO (create)", typeof(string)),
        new("end_time", "Fin événement ISO (create)", typeof(string)),
        new("event_id", "ID événement à supprimer (delete)", typeof(string)),
        new("confirmed", "true pour confirmer create/delete (N3)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "list" => ListAsync(parameters, ct),
                "today" => TodayAsync(ct),
                "create" => CreateAsync(parameters, ct),
                "delete" => DeleteAsync(parameters, ct),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : list, today, create, delete"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Agenda] {Action} échoué", action);
            return ToolResult.Failed($"Erreur agenda : {ex.Message}");
        }
    }

    /// <summary>Requête authentifiée avec rejeu automatique sur 401
    /// (jeton expiré côté Google → refresh forcé puis nouvel essai).</summary>
    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(Func<string, HttpRequestMessage> buildRequest, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        var resp = await _http.SendAsync(buildRequest(token), ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.Unauthorized) return resp;
        resp.Dispose();
        _auth.InvalidateCache();
        _logger.LogWarning("[Agenda] 401 reçu — jeton rafraîchi, nouvelle tentative");
        token = await _auth.GetAccessTokenAsync(ct);
        return await _http.SendAsync(buildRequest(token), ct);
    }

    private static HttpRequestMessage AuthedRequest(HttpMethod method, string url, string? jsonBody, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<ToolResult> ListAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var calId = p.GetValueOrDefault("calendar");
        var start = p.GetValueOrDefault("start");
        var end = p.GetValueOrDefault("end");

        var calendars = string.IsNullOrWhiteSpace(calId)
            ? await GetCalendarIdsAsync(ct)
            : new List<string> { calId };

        var all = new List<CalendarEvent>();
        foreach (var id in calendars)
        {
            var url = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(id)}/events?" +
                      $"singleEvents=true&orderBy=startTime&maxResults=50" +
                      (string.IsNullOrWhiteSpace(start) ? "" : $"&timeMin={Uri.EscapeDataString(start)}") +
                      (string.IsNullOrWhiteSpace(end) ? "" : $"&timeMax={Uri.EscapeDataString(end)}");

            using var resp = await SendWithAuthRetryAsync(
                token => AuthedRequest(HttpMethod.Get, url, null, token), ct);
            if (!resp.IsSuccessStatusCode) continue;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                    all.Add(ParseEvent(item, id));
            }
        }

        if (all.Count == 0) return ToolResult.Succeeded("Aucun événement trouvé.");
        all.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sb = new System.Text.StringBuilder($"ÉVÉNEMENTS ({all.Count}) :\n");
        foreach (var e in all.Take(30))
            sb.AppendLine($"  • [{e.Id}] {e.Start:dd/MM HH:mm} → {e.End:HH:mm}  {e.Summary}  (cal: {e.CalendarId})");
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<ToolResult> TodayAsync(CancellationToken ct)
    {
        var today = DateTime.Today;
        var start = today.ToString("yyyy-MM-ddTHH:mm:ss");
        var end = today.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss");
        var p = new Dictionary<string, string> { ["start"] = start, ["end"] = end };
        return await ListAsync(p, ct);
    }

    private async Task<ToolResult> CreateAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var confirmed = string.Equals(p.GetValueOrDefault("confirmed"), "true", StringComparison.OrdinalIgnoreCase);
        if (!confirmed) return ToolResult.Failed("Confirmation requise : relance avec confirmed=true après accord de l'utilisateur.");

        var calId = _store.Get().Google.CalendrierPrincipal;
        var ev = new
        {
            summary = p.GetValueOrDefault("summary") ?? "Sans titre",
            description = p.GetValueOrDefault("description") ?? "",
            start = new { dateTime = p.GetValueOrDefault("start_time"), timeZone = "Europe/Paris" },
            end = new { dateTime = p.GetValueOrDefault("end_time"), timeZone = "Europe/Paris" }
        };

        var json = JsonSerializer.Serialize(ev);
        using var resp = await SendWithAuthRetryAsync(
            token => AuthedRequest(HttpMethod.Post,
                $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calId)}/events",
                json, token), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return ToolResult.Failed($"Création échouée ({resp.StatusCode}) : {err}");
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var id = doc.RootElement.GetProperty("id").GetString();
        return ToolResult.Succeeded($"Événement créé : {id}. ACTION TERMINÉE.");
    }

    private async Task<ToolResult> DeleteAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var confirmed = string.Equals(p.GetValueOrDefault("confirmed"), "true", StringComparison.OrdinalIgnoreCase);
        if (!confirmed) return ToolResult.Failed("Confirmation N3 requise : relance avec confirmed=true après accord explicite.");

        var eventId = p.GetValueOrDefault("event_id");
        if (string.IsNullOrWhiteSpace(eventId)) return ToolResult.Failed("Paramètre event_id requis.");

        var calId = _store.Get().Google.CalendrierPrincipal;

        using var resp = await SendWithAuthRetryAsync(
            token => AuthedRequest(HttpMethod.Delete,
                $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calId)}/events/{Uri.EscapeDataString(eventId)}",
                null, token), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return ToolResult.Failed($"Suppression échouée ({resp.StatusCode}) : {err}");
        }
        return ToolResult.Succeeded($"Événement {eventId} supprimé. ACTION TERMINÉE.");
    }

    private async Task<List<string>> GetCalendarIdsAsync(CancellationToken ct)
    {
        using var resp = await SendWithAuthRetryAsync(
            token => AuthedRequest(HttpMethod.Get,
                "https://www.googleapis.com/calendar/v3/users/me/calendarList?maxResults=50", null, token), ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("items", out var items))
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("id", out var idProp))
                    ids.Add(idProp.GetString()!);
        return ids;
    }

    private static CalendarEvent ParseEvent(JsonElement item, string calId)
    {
        var start = item.GetProperty("start");
        var end = item.GetProperty("end");
        var startStr = start.TryGetProperty("dateTime", out var sd) ? sd.GetString()
                       : start.TryGetProperty("date", out var sd2) ? sd2.GetString() + "T00:00:00"
                       : DateTime.MinValue.ToString();
        var endStr = end.TryGetProperty("dateTime", out var ed) ? ed.GetString()
                    : end.TryGetProperty("date", out var ed2) ? ed2.GetString() + "T23:59:59"
                    : DateTime.MinValue.ToString();
        return new CalendarEvent
        {
            Id = item.GetProperty("id").GetString() ?? "",
            CalendarId = calId,
            Summary = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
            Start = DateTime.TryParse(startStr, out var st) ? st : DateTime.MinValue,
            End = DateTime.TryParse(endStr, out var et) ? et : DateTime.MinValue
        };
    }

    private sealed class CalendarEvent
    {
        public string Id { get; set; } = "";
        public string CalendarId { get; set; } = "";
        public string Summary { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
    }
}




