using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class SchedulerTool : ITool
{
    private readonly ILogger<SchedulerTool> _logger;

    public string Name => "scheduler";
    public string Description => "Gère les tâches planifiées Windows via PowerShell. Actions: list (toutes les tâches), status (état), create (crée), delete (supprime), run (exécute), enable/disable. Utilise pour planifier des exécutions automatiques.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "list, status, create, delete, run, enable, disable", typeof(string), required: true),
        new ToolParameter("name", "Nom de la tâche", typeof(string)),
        new ToolParameter("command", "Commande à exécuter (pour create)", typeof(string)),
        new ToolParameter("trigger", "Déclencheur: once, daily, weekly, at_logon, at_startup", typeof(string)),
        new ToolParameter("time", "Heure (HH:mm) ou date (YYYY-MM-DD HH:mm) pour le déclencheur", typeof(string)),
        new ToolParameter("description", "Description de la tâche (pour create)", typeof(string)),
    };

    public SchedulerTool(ILogger<SchedulerTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("name", out var taskName);
        parameters.TryGetValue("command", out var command);
        parameters.TryGetValue("trigger", out var trigger);
        parameters.TryGetValue("time", out var time);
        parameters.TryGetValue("description", out var description);

        return action?.ToLowerInvariant() switch
        {
            "list" => await RunPowerShellAsync("Get-ScheduledTask | Where-Object {$_.TaskPath -notlike '\\Microsoft\\*'} | Select-Object TaskName, State, @{N='NextRun';E={$_.NextRunTime}} | Format-Table -AutoSize"),
            "status" => await GetStatusAsync(taskName),
            "create" => await CreateTaskAsync(taskName, command, trigger, time, description),
            "delete" => await RunPowerShellAsync($"Unregister-ScheduledTask -TaskName '{taskName}' -Confirm:$false"),
            "run" => await RunPowerShellAsync($"Start-ScheduledTask -TaskName '{taskName}'"),
            "enable" => await RunPowerShellAsync($"Enable-ScheduledTask -TaskName '{taskName}'"),
            "disable" => await RunPowerShellAsync($"Disable-ScheduledTask -TaskName '{taskName}'"),
            _ => ToolResult.Failed($"Action inconnue: {action}. Valides: list, status, create, delete, run, enable, disable")
        };
    }

    private async Task<ToolResult> GetStatusAsync(string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
            return ToolResult.Failed("Paramètre 'name' requis");

        var cmd = $"Get-ScheduledTask -TaskName '{taskName}' | Select-Object TaskName, State, @{{N='NextRun';E={{$_.NextRunTime}}}}, @{{N='LastRun';E={{$_.LastRunTime}}}} | Format-List";
        return await RunPowerShellAsync(cmd);
    }

    private async Task<ToolResult> CreateTaskAsync(string? taskName, string? command, string? trigger, string? time, string? description)
    {
        if (string.IsNullOrWhiteSpace(taskName))
            return ToolResult.Failed("Paramètre 'name' requis");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Failed("Paramètre 'command' requis");

        var triggerPart = trigger?.ToLowerInvariant() switch
        {
            "once" => $"-Once -At \"{time ?? "00:00"}\"",
            "daily" => $"-Daily -At \"{time ?? "09:00"}\"",
            "weekly" => $"-Weekly -At \"{time ?? "09:00"}\"",
            "at_logon" => "-AtLogOn",
            "at_startup" => "-AtStartup",
            _ => "-Daily -At \"09:00\""
        };

        var descPart = string.IsNullOrWhiteSpace(description) ? "" : $"-Description '{description}'";
        var cmd = $"$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c {command}'; $trigger = New-ScheduledTaskTrigger {triggerPart}; Register-ScheduledTask -TaskName '{taskName}' -Action $action -Trigger $trigger {descPart} -Force";

        return await RunPowerShellAsync(cmd);
    }

    private async Task<ToolResult> RunPowerShellAsync(string command)
    {
        _logger.LogInformation("[SchedulerTool] Running: {Command}", command);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(cts.Token); }
        catch { try { process.Kill(); } catch { } }

        var outText = output.ToString().Trim();
        var errText = error.ToString().Trim();

        if (process.ExitCode == 0 && string.IsNullOrEmpty(errText))
            return ToolResult.Succeeded(string.IsNullOrEmpty(outText) ? "Opération effectuée." : outText);

        if (!string.IsNullOrEmpty(errText))
            return ToolResult.Failed($"Erreur PowerShell:\n{errText}");

        return ToolResult.Succeeded(outText);
    }
}
