using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ApiHealthCheckerTool : ToolBase
{
    private readonly IApiHealthCheckerService _service;
    private readonly ILogger<ApiHealthCheckerTool> _logger;

    public override string Name => "api_health";
    public override string Description => "Vérifier la santé d'endpoints API. Usage: api_health(action: \"check\", urls: \"https://api.example.com/health\"). Actions : check, monitor.";
    public override string Category => "web";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "check (défaut) ou monitor", typeof(string)),
        new ToolParameter("urls", "Liste d'URLs séparées par des virgules (requis)", typeof(string), required: true),
        new ToolParameter("interval_seconds", "Intervalle de vérification en secondes", typeof(string)),
        new ToolParameter("duration_minutes", "Durée du monitoring en minutes", typeof(string))
    };

    public ApiHealthCheckerTool(IApiHealthCheckerService service, ILogger<ApiHealthCheckerTool> logger)
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
        parameters.TryGetValue("action", out var actionStr);
        var urlsStr = RequireParam(parameters, "urls");
        parameters.TryGetValue("interval_seconds", out var intervalStr);
        parameters.TryGetValue("duration_minutes", out var durationStr);

        var action = (actionStr ?? "check").ToLowerInvariant();
        var urls = urlsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var intervalSeconds = 60;
        if (int.TryParse(intervalStr, out var parsedInterval) && parsedInterval > 0)
            intervalSeconds = parsedInterval;

        var durationMinutes = 5;
        if (int.TryParse(durationStr, out var parsedDuration) && parsedDuration > 0)
            durationMinutes = parsedDuration;

        return action switch
        {
            "check" => await CheckAsync(urls, ct),
            "monitor" => await MonitorAsync(urls, intervalSeconds, durationMinutes, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : check, monitor")
        };
    }

    private async Task<ToolResult> CheckAsync(string[] urls, CancellationToken ct)
    {
        var results = await _service.CheckEndpointsAsync(urls, ct);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("État des endpoints :");
        foreach (var r in results)
        {
            var state = r.IsSuccess ? "OK" : "DOWN";
            sb.AppendLine($"  {state} — {r.Url} — code {r.StatusCode} — {r.ResponseTimeMs} ms");
        }
        return Ok(sb.ToString());
    }

    private async Task<ToolResult> MonitorAsync(string[] urls, int intervalSeconds, int durationMinutes, CancellationToken ct)
    {
        var report = await _service.MonitorAsync(urls, intervalSeconds, durationMinutes, ct);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Monitoring terminé.");
        sb.AppendLine($"Endpoints surveillés : {urls.Length}");
        sb.AppendLine($"Vérifications totales : {report.Checks.Count}");
        sb.AppendLine($"Alertes : {report.Alerts.Count}");
        if (report.Alerts.Count > 0)
        {
            sb.AppendLine("Alertes :");
            foreach (var alert in report.Alerts.Take(20))
                sb.AppendLine($"  - {alert}");
        }
        return Ok(sb.ToString());
    }
}
