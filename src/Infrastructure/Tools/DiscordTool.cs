using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class DiscordTool : ITool
{
    private readonly IntegrationsStore _store;
    private readonly ILogger<DiscordTool> _logger;
    private readonly HttpClient _http;

    public DiscordTool(IntegrationsStore store, ILogger<DiscordTool> logger)
    {
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Name => "discord";
    public string Description =>
        "Discord (REST bot token) : récap mentions 24h + messages du jour par salon. " +
        "Actions : mentions, recap. Nécessite Bot Token + UserId + GuildId dans les paramètres.";
    public string Category => "social";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "mentions | recap", typeof(string), required: true)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        var settings = _store.Get().Discord;
        if (string.IsNullOrWhiteSpace(settings.BotToken) ||
            string.IsNullOrWhiteSpace(settings.UserId) ||
            string.IsNullOrWhiteSpace(settings.GuildId))
            return ToolResult.Failed("Discord non configuré : BotToken, UserId, GuildId requis.");

        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "mentions" => MentionsAsync(settings, ct),
                "recap" => RecapAsync(settings, ct),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : mentions, recap"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Discord] {Action} échoué", action);
            return ToolResult.Failed($"Erreur Discord : {ex.Message}");
        }
    }

    private async Task<ToolResult> MentionsAsync(DiscordSettings settings, CancellationToken ct)
    {
        var channels = await GetTextChannelsAsync(settings, ct);
        var since = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
        var mentionCount = 0;
        var examples = new List<string>();

        foreach (var ch in channels)
        {
            var msgs = await GetMessagesSinceAsync(ch.Id, since, settings, ct);
            foreach (var m in msgs)
            {
                if (m.Content.Contains($"<@{settings.UserId}>") || m.Content.Contains($"<@!{settings.UserId}>"))
                {
                    mentionCount++;
                    if (examples.Count < 5)
                        examples.Add($"#{ch.Name}: {Truncate(m.Content, 80)}");
                }
            }
        }

        var sb = new System.Text.StringBuilder($"MENTIONS 24H : {mentionCount}\n");
        if (examples.Count > 0)
        {
            sb.AppendLine("Exemples :");
            foreach (var e in examples) sb.AppendLine($"  • {e}");
        }
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<ToolResult> RecapAsync(DiscordSettings settings, CancellationToken ct)
    {
        var channels = await GetTextChannelsAsync(settings, ct);
        var todayStart = new DateTimeOffset(DateTime.UtcNow.Date).ToUnixTimeSeconds();
        var sb = new System.Text.StringBuilder("RÉCAP QUOTIDIEN :\n");

        foreach (var ch in channels.Take(20))
        {
            var msgs = await GetMessagesSinceAsync(ch.Id, todayStart, settings, ct);
            if (msgs.Count == 0) continue;
            sb.AppendLine($"  #{ch.Name} : {msgs.Count} messages");
            foreach (var m in msgs.Take(3))
                sb.AppendLine($"    • {m.Author?.Username ?? "?"} : {Truncate(m.Content, 60)}");
        }
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<List<DiscordChannel>> GetTextChannelsAsync(DiscordSettings s, CancellationToken ct)
    {
        var url = $"https://discord.com/api/v10/guilds/{s.GuildId}/channels";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", s.BotToken);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<DiscordChannel>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetInt32() == 0) // GUILD_TEXT
                list.Add(new DiscordChannel
                {
                    Id = item.GetProperty("id").GetString()!,
                    Name = item.GetProperty("name").GetString() ?? ""
                });
        }
        return list;
    }

    private async Task<List<DiscordMessage>> GetMessagesSinceAsync(string channelId, long since, DiscordSettings s, CancellationToken ct)
    {
        var url = $"https://discord.com/api/v10/channels/{channelId}/messages?limit=50";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", s.BotToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new List<DiscordMessage>();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<DiscordMessage>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var ts = DateTime.Parse(item.GetProperty("timestamp").GetString()!);
            if (ts.ToUniversalTime() < DateTimeOffset.FromUnixTimeSeconds(since).UtcDateTime) continue;
            list.Add(new DiscordMessage
            {
                Content = item.GetProperty("content").GetString() ?? "",
                Author = item.TryGetProperty("author", out var a) ? JsonSerializer.Deserialize<DiscordUser>(a.GetRawText()) : null
            });
        }
        return list;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class DiscordChannel { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed class DiscordMessage { public string Content { get; set; } = ""; public DiscordUser? Author { get; set; } }
    private sealed class DiscordUser { public string Username { get; set; } = ""; public string Id { get; set; } = ""; }
}




