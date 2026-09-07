using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class SiteMonitorTool : ToolBase
{
    private readonly ISiteMonitorService _service;
    private readonly ILogger<SiteMonitorTool> _logger;
    private CancellationTokenSource? _cts;

    public override string Name => "site_monitor";
    public override string Description => "Surveiller la disponibilité de sites. Usage: site_monitor(action: \"check\", urls: \"https://a.com,https://b.com\"). Actions : check, alerts, monitor. Paramètre optionnel 'stop' (true/false) pour arrêter un monitoring : site_monitor(action: \"monitor\", stop: \"true\").";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "check, alerts, monitor (requis)", typeof(string), required: true),
        new ToolParameter("urls", "Liste d'URLs séparées par des virgules (requis pour check/monitor)", typeof(string)),
        new ToolParameter("interval_seconds", "Intervalle de surveillance en secondes (défaut 300)", typeof(string)),
        new ToolParameter("stop", "true pour arrêter le monitoring en cours", typeof(string))
    };

    public SiteMonitorTool(ISiteMonitorService service, ILogger<SiteMonitorTool> logger)
        : base(logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        parameters.TryGetValue("urls", out var urlsStr);
        parameters.TryGetValue("interval_seconds", out var intervalStr);
        parameters.TryGetValue("stop", out var stopStr);

        var stop = stopStr?.ToLowerInvariant() is "true";

        if (stop && action == "monitor")
        {
            if (_cts is null)
                return Ok("Aucun monitoring en cours.");
            _cts.Cancel();
            return Ok("Monitoring arrêté.");
        }

        var intervalSeconds = 300;
        if (int.TryParse(intervalStr, out var parsed) && parsed > 0)
            intervalSeconds = parsed;

        return action switch
        {
            "check" => await CheckAsync(urlsStr, ct),
            "alerts" => await AlertsAsync(),
            "monitor" => MonitorAsync(urlsStr, intervalSeconds),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : check, alerts, monitor")
        };
    }

    private async Task<ToolResult> CheckAsync(string? urlsStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(urlsStr))
            return Fail("Le paramètre 'urls' est requis pour l'action check.");

        var urls = urlsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = await _service.CheckMultipleSitesAsync(urls, ct);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("État des sites :");
        foreach (var status in results)
        {
            var state = status.IsSuccess ? "OK" : "DOWN";
            sb.AppendLine($"  {state} — {status.Url} — {status.StatusCode} — {status.ResponseTimeMs} ms");
        }
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> AlertsAsync()
    {
        var alerts = await _service.GetAlertsAsync();
        if (alerts.Count == 0)
            return Ok("Aucune alerte enregistrée.");

        var recent = alerts.Take(20).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{recent.Count} alerte(s) récente(s) :");
        foreach (var alert in recent)
        {
            sb.AppendLine($"  [{alert.OccurredAt:HH:mm:ss}] {alert.Url} — {alert.Message}");
        }
        return Ok(sb.ToString());
    }

    private ToolResult MonitorAsync(string? urlsStr, int intervalSeconds)
    {
        if (string.IsNullOrWhiteSpace(urlsStr))
            return Fail("Le paramètre 'urls' est requis pour l'action monitor.");

        var urlList = urlsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (urlList.Count == 0)
            return Fail("Aucune URL fournie.");

        var newCts = new CancellationTokenSource();
        var old = System.Threading.Interlocked.Exchange(ref _cts, newCts);
        old?.Cancel();
        old?.Dispose();

        _ = Task.Run(() => _service.StartMonitoringAsync(urlList, intervalSeconds, newCts.Token));

        return Ok($"Monitoring démarré ({urlList.Count} sites, intervalle {intervalSeconds}s). Pour arrêter : site_monitor(action: \"monitor\", stop: \"true\").");
    }
}
