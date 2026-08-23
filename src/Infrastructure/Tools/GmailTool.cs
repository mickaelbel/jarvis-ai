using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class GmailTool : ITool
{
    private readonly GoogleAuthHelper _auth;
    private readonly IntegrationsStore _store;
    private readonly ILogger<GmailTool> _logger;
    private readonly HttpClient _http;

    public GmailTool(GoogleAuthHelper auth, IntegrationsStore store, ILogger<GmailTool> logger)
    {
        _auth = auth;
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "mail";
    public string Description =>
        "Gmail via API REST (OAuth Google partagé) : liste les derniers mails, envoie un mail. " +
        "Actions : list, send. Envoi = confirmation N3.";
    public string Category => "mail";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public string? WaitingPhrase => "J'accède à Gmail…";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "list | send", typeof(string), required: true),
        new("max", "Nombre de mails à lire (list, défaut 5)", typeof(string)),
        new("query", "Requête Gmail (ex: is:unread newer_than:1d)", typeof(string)),
        new("to", "Destinataire (send)", typeof(string)),
        new("subject", "Sujet (send)", typeof(string)),
        new("body", "Corps texte (send)", typeof(string)),
        new("confirmed", "true pour confirmer l'envoi (N3)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "list" => ListAsync(parameters, ct),
                "send" => SendAsync(parameters, ct),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : list, send"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Gmail] {Action} échoué", action);
            return ToolResult.Failed($"Erreur Gmail : {ex.Message}");
        }
    }

    private async Task<ToolResult> ListAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        var max = int.TryParse(p.GetValueOrDefault("max"), out var m) ? m : 5;
        var query = p.GetValueOrDefault("query") ?? "newer_than:1d";

        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults={max}&q={Uri.EscapeDataString(query)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return ToolResult.Failed($"List failed ({resp.StatusCode}) : {err}");
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("messages", out var messages))
            return ToolResult.Succeeded("Aucun mail trouvé.");

        var sb = new System.Text.StringBuilder($"DERNIERS MAILS ({messages.GetArrayLength()}) :\n");
        foreach (var msg in messages.EnumerateArray().Take(max))
        {
            var id = msg.GetProperty("id").GetString()!;
            var detail = await GetMessageDetailAsync(id, token, ct);
            sb.AppendLine($"  • {detail}");
        }
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<string> GetMessageDetailAsync(string id, string token, CancellationToken ct)
    {
        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}?format=metadata&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Date";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return $"[{id}] erreur";
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var headers = doc.RootElement.TryGetProperty("payload", out var p) && p.TryGetProperty("headers", out var h) ? h : default;
        string subject = "", from = "", date = "";
        if (headers.ValueKind != JsonValueKind.Undefined)
        {
            foreach (var hdr in headers.EnumerateArray())
            {
                var name = hdr.GetProperty("name").GetString() ?? "";
                var value = hdr.GetProperty("value").GetString() ?? "";
                if (name.Equals("Subject", StringComparison.OrdinalIgnoreCase)) subject = value;
                else if (name.Equals("From", StringComparison.OrdinalIgnoreCase)) from = value;
                else if (name.Equals("Date", StringComparison.OrdinalIgnoreCase)) date = value;
            }
        }
        var snippet = doc.RootElement.TryGetProperty("snippet", out var sn) ? sn.GetString() ?? "" : "";
        return $"[{id}] {date} | {from} | {subject} — {snippet[..Math.Min(80, snippet.Length)]}";
    }

    private async Task<ToolResult> SendAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var confirmed = string.Equals(p.GetValueOrDefault("confirmed"), "true", StringComparison.OrdinalIgnoreCase);
        if (!confirmed) return ToolResult.Failed("Confirmation N3 requise : relance avec confirmed=true après accord explicite.");

        var token = await _auth.GetAccessTokenAsync(ct);
        var to = p.GetValueOrDefault("to");
        var subject = p.GetValueOrDefault("subject");
        var body = p.GetValueOrDefault("body");
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            return ToolResult.Failed("Paramètres to, subject, body requis.");

        var mime = $"To: {to}\r\nSubject: {subject}\r\nContent-Type: text/plain; charset=\"UTF-8\"\r\n\r\n{body}";
        var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(mime))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var payload = JsonSerializer.Serialize(new { raw });
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return ToolResult.Failed($"Envoi échoué ({resp.StatusCode}) : {err}");
        }
        return ToolResult.Succeeded("Mail envoyé. ACTION TERMINÉE.");
    }
}




