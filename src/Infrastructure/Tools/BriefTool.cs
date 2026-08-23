using JarvisAI.Application.Agents;
using JarvisAI.Application.Reminders;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

// Brief matinal (portage tools/brief.py) : heure/date + météo + deadlines iCal
// + suivi contenus en retard + rappels du jour. Chaque bloc est optionnel :
// une erreur dans un bloc n'empêche pas les autres.
public sealed class BriefTool : ITool
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BriefTool> _logger;

    public BriefTool(IServiceProvider services, ILogger<BriefTool> logger)
    {
        _services = services;
        _logger = logger;
    }

    public string Name => "brief";
    public string Description =>
        "Brief matinal complet : date/heure, météo, deadlines iCal à venir, contenus vidéo en retard, mails récents, rappels du jour.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "Je prépare ton brief…";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "matin (défaut)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        var blocks = new List<Task<string?>>
        {
            BlockDateHeure(),
            BlockMeteo(ct),
            BlockDeadlines(ct),
            BlockSuivi(ct),
            BlockMails(ct),
            BlockRappels()
        };

        foreach (var block in await Task.WhenAll(blocks))
        {
            if (!string.IsNullOrWhiteSpace(block)) sb.AppendLine(block).AppendLine();
        }

        return ToolResult.Succeeded(sb.ToString().Trim());
    }

    private static Task<string?> BlockDateHeure() => Task.FromResult<string?>(
        $"🗓 {DateTime.Now:dddd dd MMMM yyyy — HH:mm}".ToUpperInvariant() is var s ? s : null);

    private async Task<string?> BlockMeteo(CancellationToken ct)
    {
        try
        {
            var tool = _services.GetService(typeof(IToolRegistry)) is IToolRegistry reg ? reg.GetByName("weather") : null;
            if (tool is null) return null;
            var res = await tool.ExecuteAsync(context: null!, new Dictionary<string, string> { ["action"] = "current" }, ct);
            return res.Success ? $"🌤 MÉTÉO :\n{res.Output}" : null;
        }
        catch { return null; }
    }

    private async Task<string?> BlockDeadlines(CancellationToken ct)
    {
        try
        {
            var tool = _services.GetService(typeof(IToolRegistry)) is IToolRegistry reg ? reg.GetByName("loopstr") : null;
            if (tool is null) return null;
            var res = await tool.ExecuteAsync(null!, new Dictionary<string, string> { ["action"] = "deadlines", ["days"] = "7" }, ct);
            return res.Success ? $"📅 DEADLINES ICAL :\n{res.Output}" : null;
        }
        catch { return null; }
    }

    private async Task<string?> BlockSuivi(CancellationToken ct)
    {
        try
        {
            var tool = _services.GetService(typeof(IToolRegistry)) is IToolRegistry reg ? reg.GetByName("suivi") : null;
            if (tool is null) return null;
            var res = await tool.ExecuteAsync(null!, new Dictionary<string, string> { ["action"] = "oujenesuis" }, ct);
            if (!res.Success) return null;
            // Ne garder que la partie retards pour le brief
            var lines = res.Output.Split('\n').Where(l => l.Contains("EN RETARD") || l.Contains("•")).Take(6);
            var joined = string.Join("\n", lines);
            return string.IsNullOrWhiteSpace(joined) ? null : $"🎬 CONTENUS EN RETARD :\n{joined}";
        }
        catch { return null; }
    }

    private async Task<string?> BlockMails(CancellationToken ct)
    {
        try
        {
            var tool = _services.GetService(typeof(IToolRegistry)) is IToolRegistry reg ? reg.GetByName("mail") : null;
            if (tool is null) return null;
            var res = await tool.ExecuteAsync(null!, new Dictionary<string, string> { ["action"] = "list", ["max"] = "3" }, ct);
            return res.Success ? $"📧 DERNIERS MAILS :\n{res.Output}" : null;
        }
        catch { return null; }
    }

    private Task<string?> BlockRappels()
    {
        try
        {
            var svc = _services.GetService(typeof(IReminderService)) as IReminderService;
            if (svc is null) return Task.FromResult<string?>(null);
            var today = svc.GetActive().Where(r => r.DueAt.Date <= DateTime.Today && !r.Completed).Take(5).ToList();
            if (today.Count == 0) return Task.FromResult<string?>(null);
            return Task.FromResult<string?>($"⏰ RAPPELS :\n" +
                string.Join("\n", today.Select(r => $"  • {r.DueAt:HH:mm} — {r.Text}")));
        }
        catch { return Task.FromResult<string?>(null); }
    }
}