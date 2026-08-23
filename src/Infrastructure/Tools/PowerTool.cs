using System.Diagnostics;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Contrôle de l'alimentation du PC. Extinction/redémarrage = N3 : confirmation
/// obligatoire à chaque fois (géré par SecurityManager + ToolSafetyPolicy).
/// L'extinction est programmée avec un délai de 30 s annulable (« annule l'extinction »).
/// </summary>
public sealed class PowerTool : ITool
{
    private readonly ILogger<PowerTool> _logger;
    public string Name => "power";
    public string Description => "Contrôle de l'alimentation du PC. Actions : shutdown (éteint le PC après 30s, DEMANDE TOUJOURS confirmation), cancel_shutdown (annule une extinction programmée), restart (redémarre après 30s, confirmation obligatoire), sleep (veille), lock (verrouille la session), status (état de l'extinction programmée).";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Action : shutdown, cancel_shutdown, restart, sleep, lock, status", typeof(string), required: true)
    };

    public PowerTool(ILogger<PowerTool> logger) => _logger = logger;

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        try
        {
            return await Task.Run(() => action?.ToLowerInvariant() switch
            {
                "shutdown" => ScheduleShutdown("/s /t 30", "Le PC s'éteindra dans 30 secondes."),
                "restart" => ScheduleShutdown("/r /t 30", "Le PC redémarrera dans 30 secondes."),
                "cancel_shutdown" => CancelShutdown(),
                "sleep" => RunCommand("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0", "Mise en veille effectuée."),
                "lock" => RunCommand("rundll32.exe", "user32.dll,LockWorkStation", "Session verrouillée."),
                "status" => Status(),
                _ => ToolResult.Failed($"Action inconnue : {action}. Valides : shutdown, cancel_shutdown, restart, sleep, lock, status")
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Power] Action {Action} échouée", action);
            return ToolResult.Failed($"Erreur power : {ex.Message}");
        }
    }

    private ToolResult ScheduleShutdown(string arguments, string message)
    {
        var psi = new ProcessStartInfo("shutdown.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
        _logger.LogWarning("[Power] Extinction programmée ({Arguments}) — annulable via cancel_shutdown", arguments);
        return ToolResult.Succeeded($"{message} ACTION TERMINÉE. Dis « annule l'extinction » pour annuler.");
    }

    private ToolResult CancelShutdown()
    {
        var psi = new ProcessStartInfo("shutdown.exe", "/a")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
        // Code 1115 (0x45B) = aucune extinction programmée.
        if (p is { ExitCode: 1115 })
            return ToolResult.Succeeded("Aucune extinction n'était programmée. ACTION TERMINÉE.");
        _logger.LogInformation("[Power] Extinction annulée");
        return ToolResult.Succeeded("Extinction annulée, le PC reste allumé. ACTION TERMINÉE.");
    }

    private static ToolResult Status()
    {
        var procs = Process.GetProcessesByName("shutdown");
        return ToolResult.Succeeded(procs.Length > 0
            ? "Une extinction est programmée (elle peut être annulée avec cancel_shutdown)."
            : "Aucune extinction programmée. ACTION TERMINÉE.");
    }

    private static ToolResult RunCommand(string fileName, string arguments, string successMessage)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        _ = Process.Start(psi);
        return ToolResult.Succeeded($"{successMessage} ACTION TERMINÉE.");
    }
}
