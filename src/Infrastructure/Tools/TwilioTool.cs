using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class TwilioTool : ITool
{
    private readonly IntegrationsStore _store;
    private readonly ILogger<TwilioTool> _logger;
    private readonly HttpClient _http;

    public TwilioTool(IntegrationsStore store, ILogger<TwilioTool> logger)
    {
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "twilio";
    public string Description =>
        "Twilio appels V1 : joue un message vocal (TwiML inline). Action : call. " +
        "Simulation mode via paramètre simulation=true. Préfixes surtaxés bloqués.";
    public string Category => "phone";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public string? WaitingPhrase => "J'appelle via Twilio…";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "call | status", typeof(string), required: true),
        new("to", "Numéro destination (E.164 +336...)", typeof(string)),
        new("message", "Message à dire (call)", typeof(string)),
        new("simulation", "true pour simuler sans appeler", typeof(string)),
        new("confirmed", "true pour confirmer (N3)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        var settings = _store.Get().Twilio;

        if (string.IsNullOrWhiteSpace(settings.AccountSid) ||
            string.IsNullOrWhiteSpace(settings.AuthToken) ||
            string.IsNullOrWhiteSpace(settings.Numero))
            return ToolResult.Failed("Twilio non configuré : AccountSid, AuthToken, Numero requis.");

        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "call" => CallAsync(settings, parameters, ct),
                "status" => StatusAsync(settings, ct),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : call, status"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Twilio] {Action} échoué", action);
            return ToolResult.Failed($"Erreur Twilio : {ex.Message}");
        }
    }

    private async Task<ToolResult> CallAsync(TwilioSettings settings, IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var confirmed = string.Equals(p.GetValueOrDefault("confirmed"), "true", StringComparison.OrdinalIgnoreCase);
        if (!confirmed) return ToolResult.Failed("Confirmation N3 requise : relance avec confirmed=true après accord explicite.");

        var to = p.GetValueOrDefault("to");
        var message = p.GetValueOrDefault("message");
        var simulation = string.Equals(p.GetValueOrDefault("simulation"), "true", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(message))
            return ToolResult.Failed("Paramètres to et message requis.");

        var cleanTo = to.Replace(" ", "").Replace("-", "");
        foreach (var pref in settings.PrefixesInterdits)
            if (cleanTo.StartsWith(pref, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Failed($"Numéro bloqué (préfixe surtaxé {pref}).");

        var twiml = $"<Response><Say language=\"fr-FR\">{System.Security.SecurityElement.Escape(message)}</Say></Response>";
        var url = $"https://api.twilio.com/2010-04-01/Accounts/{settings.AccountSid}/Calls.json";

        if (simulation)
        {
            _logger.LogInformation("[Twilio] SIMULATION appel vers {To} : {Msg}", to, message);
            return ToolResult.Succeeded($"[SIMULATION] Appel vers {to} avec message : {message}");
        }

        var form = new Dictionary<string, string>
        {
            ["To"] = cleanTo,
            ["From"] = settings.Numero,
            ["Twiml"] = twiml
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{settings.AccountSid}:{settings.AuthToken}")));
        req.Content = new FormUrlEncodedContent(form);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return ToolResult.Failed($"Appel échoué ({resp.StatusCode}) : {body}");

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var sid = doc.RootElement.GetProperty("sid").GetString();
        _logger.LogInformation("[Twilio] Appel créé {Sid} vers {To}", sid, to);
        return ToolResult.Succeeded($"Appel lancé (SID: {sid}). ACTION TERMINÉE.");
    }

    private async Task<ToolResult> StatusAsync(TwilioSettings settings, CancellationToken ct)
    {
        var url = $"https://api.twilio.com/2010-04-01/Accounts/{settings.AccountSid}/Calls.json?PageSize=10";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{settings.AccountSid}:{settings.AuthToken}")));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return ToolResult.Failed($"Status failed ({resp.StatusCode})");
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("calls", out var calls)) return ToolResult.Succeeded("Aucun appel récent.");
        var sb = new System.Text.StringBuilder("DERNIERS APPELS :\n");
        foreach (var c in calls.EnumerateArray().Take(10))
        {
            var sid = c.GetProperty("sid").GetString() ?? "";
            var to = c.GetProperty("to").GetString() ?? "";
            var status = c.GetProperty("status").GetString() ?? "";
            var dur = c.TryGetProperty("duration", out var d) ? d.GetString() : "?";
            sb.AppendLine($"  {sid} → {to} : {status} ({dur}s)");
        }
        return ToolResult.Succeeded(sb.ToString());
    }
}




