using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class LogFileMonitorTool : ToolBase
{
    private readonly ILogFileMonitorService _service;

    public override string Name => "log_monitor";
    public override string Description => "Surveille des fichiers de log à la recherche de mots-clés (erreurs, exceptions...) et renvoie les nouvelles alertes détectées. Usage: log_monitor(action: \"scan\", log_path: \"C:/logs/app.log\", keywords: \"error,exception\") ou log_monitor(action: \"status\").";
    public override string Category => "system";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "scan (défaut) ou status", typeof(string)),
        new ToolParameter("log_path", "Chemin du fichier de log ou répertoire contenant des .log", typeof(string), required: true),
        new ToolParameter("keywords", "Mots-clés séparés par des virgules (défaut: error, exception, fatal, critical)", typeof(string)),
        new ToolParameter("state_file", "Fichier d'état des offsets (défaut: %LocalAppData%\\JarvisAI\\search_state\\logs.json)", typeof(string)),
    };

    public LogFileMonitorTool(ILogFileMonitorService service, ILogger<LogFileMonitorTool> logger)
        : base(logger)
    {
        _service = service;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        parameters.TryGetValue("action", out var actionStr);
        var action = (actionStr ?? "scan").ToLowerInvariant();

        return action switch
        {
            "scan" => await ScanAsync(parameters, ct),
            "status" => Status(),
            _ => Fail($"Action inconnue : '{actionStr}'. Actions valides : scan, status")
        };
    }

    private async Task<ToolResult> ScanAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var logPath = RequireParam(parameters, "log_path");
        parameters.TryGetValue("keywords", out var keywordsStr);
        parameters.TryGetValue("state_file", out var stateFile);

        string[]? keywords = null;
        if (!string.IsNullOrWhiteSpace(keywordsStr))
        {
            var split = keywordsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (split.Length > 0)
                keywords = split;
        }

        var defaultState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "search_state", "logs.json");

        var result = await _service.ScanAsync(logPath, keywords, string.IsNullOrWhiteSpace(stateFile) ? defaultState : stateFile, ct);
        if (!result.Success)
            return Fail($"Échec du scan des logs : {result.ErrorMessage}");

        var sb = new StringBuilder();
        sb.AppendLine($"Scan terminé : {result.FilesScanned} fichier(s) analysé(s), {result.BytesScanned} octet(s) lus.");
        if (result.NewAlerts.Count == 0)
        {
            sb.AppendLine("Aucune nouvelle alerte détectée.");
            return Ok(sb.ToString());
        }

        sb.AppendLine($"{result.NewAlerts.Count} nouvelle(s) alerte(s) :");
        foreach (var alert in result.NewAlerts)
            sb.AppendLine($"  {alert.FilePath}:{alert.LineNumber} [{alert.MatchedKeyword}] {alert.Line}");

        return Ok(sb.ToString());
    }

    private ToolResult Status()
    {
        var alerts = _service.GetRecentAlerts().TakeLast(20).ToList();
        var sb = new StringBuilder();
        if (alerts.Count == 0)
        {
            sb.AppendLine("Aucune alerte enregistrée.");
            return Ok(sb.ToString());
        }

        sb.AppendLine($"{alerts.Count} dernière(s) alerte(s) :");
        foreach (var alert in alerts)
            sb.AppendLine($"  {alert.At:yyyy-MM-dd HH:mm:ss} | {alert.FilePath}:{alert.LineNumber} [{alert.MatchedKeyword}] {alert.Line}");

        return Ok(sb.ToString());
    }
}