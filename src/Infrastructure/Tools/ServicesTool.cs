using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ServicesTool : ITool
{
    private readonly ILogger<ServicesTool> _logger;

    public string Name => "windows_services";
    public string Description => "Gère les services Windows via PowerShell. Actions: list (tous les services), status (état d'un service), start (démarre), stop (arrête), restart (redémarre), find (cherche par nom). Utilise pour gérer WAMP, Docker, Apache, MySQL, etc.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "list, status, start, stop, restart, find", typeof(string), required: true),
        new ToolParameter("name", "Nom du service (ex: 'wampapache', 'Docker', 'MySQL')", typeof(string)),
        new ToolParameter("search", "Terme de recherche pour find", typeof(string)),
    };

    public ServicesTool(ILogger<ServicesTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("name", out var serviceName);
        parameters.TryGetValue("search", out var search);

        return action?.ToLowerInvariant() switch
        {
            "list" => await RunPSAsync("Get-Service | Where-Object {$_.Status -eq 'Running'} | Select-Object Name, DisplayName, Status | Format-Table -AutoSize; Write-Host '\\n--- Arrêtés ---'; Get-Service | Where-Object {$_.Status -eq 'Stopped'} | Select-Object Name, DisplayName, Status | Format-Table -AutoSize"),
            "status" => await RunPSAsync($"Get-Service -Name '{serviceName}' | Select-Object Name, DisplayName, Status, StartType | Format-List"),
            "start" => await RunPSAsync($"Start-Service -Name '{serviceName}'; Write-Host 'Démarré.'; Get-Service -Name '{serviceName}' | Select-Object Name, Status | Format-Table"),
            "stop" => await RunPSAsync($"Stop-Service -Name '{serviceName}' -Force; Write-Host 'Arrêté.'; Get-Service -Name '{serviceName}' | Select-Object Name, Status | Format-Table"),
            "restart" => await RunPSAsync($"Restart-Service -Name '{serviceName}' -Force; Write-Host 'Redémarré.'; Get-Service -Name '{serviceName}' | Select-Object Name, Status | Format-Table"),
            "find" => await RunPSAsync($"Get-Service | Where-Object {{$_.Name -like '*{search}*' -or $_.DisplayName -like '*{search}*'}} | Select-Object Name, DisplayName, Status | Format-Table -AutoSize"),
            _ => ToolResult.Failed($"Action inconnue: {action}. Valides: list, status, start, stop, restart, find")
        };
    }

    private async Task<ToolResult> RunPSAsync(string command)
    {
        _logger.LogInformation("[ServicesTool] Running: {Command}", command);

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
            return ToolResult.Failed($"Erreur:\n{errText}");

        return ToolResult.Succeeded(outText);
    }
}
