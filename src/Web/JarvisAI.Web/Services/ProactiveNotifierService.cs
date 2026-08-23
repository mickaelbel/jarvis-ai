using System.Text.RegularExpressions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Veille proactive : surveille les nouveaux mails non lus (Gmail) et les
/// mentions Discord, puis les annonce vocalement via ProactiveAnnouncer —
/// uniquement ce qui est nouveau depuis la vérification précédente, et
/// seulement quand l'utilisateur ne parle pas (file FIFO du announcer).
/// </summary>
public sealed partial class ProactiveNotifierService : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(6);

    private readonly Lazy<IToolRegistry> _tools;
    private readonly ProactiveAnnouncer _announcer;
    private readonly IntegrationsStore _integrations;
    private readonly ILogger<ProactiveNotifierService> _logger;

    private readonly HashSet<string> _seenMailIds = new(StringComparer.Ordinal);
    private bool _mailSeeded;
    private int _lastMentionCount = -1;

    public ProactiveNotifierService(
        Lazy<IToolRegistry> tools,
        ProactiveAnnouncer announcer,
        IntegrationsStore integrations,
        ILogger<ProactiveNotifierService> logger)
    {
        _tools = tools;
        _announcer = announcer;
        _integrations = integrations;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisser l'application finir de démarrer avant la première sonde.
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckMailsSafeAsync();
            await CheckMentionsSafeAsync();
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckMailsSafeAsync()
    {
        try
        {
            var google = _integrations.Get().Google;
            if (string.IsNullOrWhiteSpace(google.RefreshToken)) return;

            var mailTool = _tools.Value.GetByName("mail");
            if (mailTool is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await mailTool.ExecuteAsync(
                new AgentContext("[Proactif] veille des mails non lus", "system"),
                new Dictionary<string, string>
                {
                    ["action"] = "list",
                    ["query"] = "is:unread newer_than:1d",
                    ["max"] = "15"
                }, timeout.Token);
            if (!result.Success || string.IsNullOrEmpty(result.Output)) return;

            var ids = MailIdRegex().Matches(result.Output)
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            if (!_mailSeeded)
            {
                _seenMailIds.UnionWith(ids); // première passe : référence silencieuse
                _mailSeeded = true;
                return;
            }

            var fresh = ids.Where(id => !_seenMailIds.Contains(id)).ToList();
            foreach (var id in fresh) _seenMailIds.Add(id);
            if (_seenMailIds.Count > 500) _seenMailIds.Clear();

            if (fresh.Count > 0)
            {
                var sender = ExtractFirstSender(result.Output);
                _announcer.Enqueue(fresh.Count == 1
                    ? $"Nouveau mail non lu{sender}."
                    : $"{fresh.Count} nouveaux mails non lus{sender}.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Proactif] veille mails impossible");
        }
    }

    [GeneratedRegex(@"\[([A-Za-z0-9_\-]{8,})\]")]
    private static partial Regex MailIdRegex();

    private static string ExtractFirstSender(string output)
    {
        var m = SenderRegex().Match(output);
        return m.Success ? $" de {m.Groups[1].Value.Trim()}" : "";
    }

    [GeneratedRegex(@"\]\s*[^|\n]*\|\s*([^|]+)\|")]
    private static partial Regex SenderRegex();

    private async Task CheckMentionsSafeAsync()
    {
        try
        {
            var discord = _integrations.Get().Discord;
            if (string.IsNullOrWhiteSpace(discord.BotToken) || string.IsNullOrWhiteSpace(discord.UserId)) return;

            var tool = _tools.Value.GetByName("discord");
            if (tool is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await tool.ExecuteAsync(
                new AgentContext("[Proactif] veille des mentions Discord", "system"),
                new Dictionary<string, string> { ["action"] = "mentions" }, timeout.Token);
            if (!result.Success || string.IsNullOrEmpty(result.Output)) return;

            var match = MentionCountRegex().Match(result.Output);
            if (!match.Success) return;
            var count = int.Parse(match.Groups[1].Value);

            if (_lastMentionCount < 0)
                _logger.LogInformation("[Proactif] veille Discord initialisée ({Count} mentions/24h)", count);
            else if (count > _lastMentionCount && count > 0)
            {
                var delta = count - _lastMentionCount;
                _announcer.Enqueue(delta == 1
                    ? "Tu as une nouvelle mention Discord."
                    : $"{delta} nouvelles mentions Discord.");
            }
            _lastMentionCount = count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Proactif] veille mentions impossible");
        }
    }

    [GeneratedRegex(@"MENTIONS\s*24H\s*:\s*(\d+)")]
    private static partial Regex MentionCountRegex();
}
