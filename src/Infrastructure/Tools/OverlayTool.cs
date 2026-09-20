using JarvisAI.Application.Agents;
using JarvisAI.Application.Overlay;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// AR overlay tool for the agent.
/// Actions: show, toast, stats, clear, start, stop.
/// </summary>
public sealed class OverlayTool : ITool
{
    private readonly IOverlayService _overlay;
    private readonly ILogger<OverlayTool> _logger;

    public OverlayTool(IOverlayService overlay, ILogger<OverlayTool> logger)
    {
        _overlay = overlay;
        _logger = logger;
    }

    public string Name => "overlay_ar";
    public string Description =>
        "Overlay AR : affiche des informations au-dessus de toutes les applications. " +
        "Actions : start (demarrer), stop (arreter), show <texte> (afficher), " +
        "toast <texte> (notification rapide), stats (stats systeme), clear (tout effacer).";
    public string Category => "ui";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "start | stop | show | toast | stats | clear", typeof(string), required: true),
        new("texte", "Texte a afficher (pour show/toast)", typeof(string), required: false),
        new("style", "Style : default | alert | success | info", typeof(string), required: false),
        new("duree", "Duree en ms (0 = permanent)", typeof(string), required: false)
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("texte", out var texte);
        parameters.TryGetValue("style", out var style);
        parameters.TryGetValue("duree", out var dureeStr);

        try
        {
            return (action?.ToLowerInvariant().Trim()) switch
            {
                "start" => await Start(),
                "stop" => await Stop(),
                "show" => Show(texte, style, dureeStr),
                "toast" => Toast(texte, style, dureeStr),
                "stats" => await Stats(),
                "clear" => Clear(),
                _ => ToolResult.Failed("Action inconnue : start, stop, show, toast, stats, clear.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OverlayTool] Error");
            return ToolResult.Failed($"Erreur : {ex.Message}");
        }
    }

    private async Task<ToolResult> Start()
    {
        if (_overlay.IsRunning)
            return ToolResult.Succeeded("Overlay deja en cours d'execution.");

        await _overlay.StartAsync();
        return ToolResult.Succeeded("Overlay AR demarré. Les informations s'affichent au-dessus de toutes les applications.");
    }

    private async Task<ToolResult> Stop()
    {
        if (!_overlay.IsRunning)
            return ToolResult.Succeeded("Overlay deja arrete.");

        await _overlay.StopAsync();
        return ToolResult.Succeeded("Overlay AR arrete.");
    }

    private ToolResult Show(string? texte, string? style, string? dureeStr)
    {
        if (string.IsNullOrWhiteSpace(texte))
            return ToolResult.Failed("Precisez le texte a afficher.");

        if (!_overlay.IsRunning)
            return ToolResult.Failed("L'overlay n'est pas demarré. Utilisez 'start' d'abord.");

        var duration = int.TryParse(dureeStr, out var d) ? d : 5000;
        _overlay.Show(new OverlayContent
        {
            Text = texte,
            Style = style ?? "default",
            DurationMs = duration
        });

        return ToolResult.Succeeded("Affiche sur l'overlay.");
    }

    private ToolResult Toast(string? texte, string? style, string? dureeStr)
    {
        if (string.IsNullOrWhiteSpace(texte))
            return ToolResult.Failed("Precisez le texte du toast.");

        if (!_overlay.IsRunning)
            return ToolResult.Failed("L'overlay n'est pas demarré.");

        var duration = int.TryParse(dureeStr, out var d) ? d : 3000;
        _overlay.Toast(texte, style ?? "default", duration);
        return ToolResult.Succeeded("Toast affiche.");
    }

    private async Task<ToolResult> Stats()
    {
        if (!_overlay.IsRunning)
            return ToolResult.Failed("L'overlay n'est pas demarré.");

        // Get system stats
        var cpu = GetCpuUsage();
        var ram = GetRamUsage();

        _overlay.ShowSystemStats(cpu, ram);
        return ToolResult.Succeeded($"Stats affichees : CPU {cpu:F1}%, RAM {ram:F1}%");
    }

    private ToolResult Clear()
    {
        _overlay.Clear();
        return ToolResult.Succeeded("Overlay efface.");
    }

    private static float GetCpuUsage()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            return (float)proc.TotalProcessorTime.TotalMilliseconds /
                   (Environment.ProcessorCount * Environment.TickCount) * 100;
        }
        catch { return 0; }
    }

    private static float GetRamUsage()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return (float)proc.WorkingSet64 / total * 100;
        }
        catch { return 0; }
    }
}
