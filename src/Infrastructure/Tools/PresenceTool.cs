using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Presence;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Active/désactive/interroge la détection de présence par ping du téléphone.
/// </summary>
public sealed class PresenceTool : ITool
{
    private readonly PingPresenceMonitor? _monitor;
    private readonly ILogger<PresenceTool> _logger;

    public string Name => "presence";
    public string Description => "Détection de présence par ping du téléphone. Actions : start (active), stop (désactive), status (état actuel : téléphone présent ou absent).";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action : start, stop, status", typeof(string), required: true)
    };

    public PresenceTool(PingPresenceMonitor? monitor, ILogger<PresenceTool> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        if (_monitor is null || !_monitor.IsConfiguredPublic)
            return Task.FromResult(ToolResult.Failed("Détection non configurée : renseigne JarvisAI:Presence:PhoneIp (IP fixe du téléphone en DHCP) dans la configuration."));

        parameters.TryGetValue("action", out var action);
        return Task.FromResult(action?.ToLowerInvariant() switch
        {
            "start" => Start(),
            "stop" => Stop(),
            "status" => ToolResult.Succeeded(_monitor.GetStatus()),
            _ => ToolResult.Failed($"Action inconnue : {action}. Valides : start, stop, status")
        });
    }

    private ToolResult Start()
    {
        _monitor!.SetEnabled(true);
        _logger.LogInformation("[PresenceTool] Détection activée");
        return ToolResult.Succeeded("Détection de présence activée. ACTION TERMINÉE.");
    }

    private ToolResult Stop()
    {
        _monitor!.SetEnabled(false);
        _logger.LogInformation("[PresenceTool] Détection désactivée");
        return ToolResult.Succeeded("Détection de présence désactivée. ACTION TERMINÉE.");
    }
}
