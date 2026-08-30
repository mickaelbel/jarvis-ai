using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IAutomatedReportingService
{
    ReportResult GenerateReport(ReportType type, DateTime? from = null, DateTime? to = null);
    IReadOnlyList<ReportSchedule> GetSchedules();
    string CreateSchedule(string name, ReportType type, ReportFrequency frequency, string? recipient = null);
    void DeleteSchedule(string scheduleId);
    void EnableSchedule(string scheduleId);
    void DisableSchedule(string scheduleId);
}

public sealed class AutomatedReportingService : IAutomatedReportingService
{
    private readonly ILogger<AutomatedReportingService> _logger;
    private readonly ISessionAnalyticsService _analytics;
    private readonly ICostTrackingService _costTracking;
    private readonly string _storagePath;
    private readonly List<ReportSchedule> _schedules = new();

    public AutomatedReportingService(
        ILogger<AutomatedReportingService> logger,
        ISessionAnalyticsService analytics,
        ICostTrackingService costTracking)
    {
        _logger = logger;
        _analytics = analytics;
        _costTracking = costTracking;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "report_schedules.json");
        Load();
    }

    public ReportResult GenerateReport(ReportType type, DateTime? from = null, DateTime? to = null)
    {
        var startDate = from ?? DateTime.UtcNow.AddDays(-7);
        var endDate = to ?? DateTime.UtcNow;

        var content = type switch
        {
            ReportType.DailyActivity => GenerateDailyActivityReport(startDate, endDate),
            ReportType.WeeklySummary => GenerateWeeklySummaryReport(startDate, endDate),
            ReportType.CostAnalysis => GenerateCostAnalysisReport(startDate, endDate),
            ReportType.ToolUsage => GenerateToolUsageReport(startDate, endDate),
            ReportType.SystemHealth => GenerateSystemHealthReport(),
            _ => "Type de rapport non supporté"
        };

        var result = new ReportResult
        {
            Type = type,
            Content = content,
            GeneratedAt = DateTime.UtcNow,
            PeriodStart = startDate,
            PeriodEnd = endDate
        };

        _logger.LogInformation("[Report] Generated {Type} report", type);
        return result;
    }

    public IReadOnlyList<ReportSchedule> GetSchedules() => _schedules.ToList();

    public string CreateSchedule(string name, ReportType type, ReportFrequency frequency, string? recipient = null)
    {
        var schedule = new ReportSchedule
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Type = type,
            Frequency = frequency,
            Recipient = recipient,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            NextRun = CalculateNextRun(frequency)
        };

        _schedules.Add(schedule);
        Save();
        return schedule.Id;
    }

    public void DeleteSchedule(string scheduleId)
    {
        _schedules.RemoveAll(s => s.Id == scheduleId);
        Save();
    }

    public void EnableSchedule(string scheduleId)
    {
        var schedule = _schedules.FirstOrDefault(s => s.Id == scheduleId);
        if (schedule is not null) { schedule.IsEnabled = true; Save(); }
    }

    public void DisableSchedule(string scheduleId)
    {
        var schedule = _schedules.FirstOrDefault(s => s.Id == scheduleId);
        if (schedule is not null) { schedule.IsEnabled = false; Save(); }
    }

    private string GenerateDailyActivityReport(DateTime from, DateTime to)
    {
        var events = _analytics.GetEvents(from, to);
        var topCommands = _analytics.GetTopCommands(5);
        var topTools = _analytics.GetTopTools(5);

        var sb = new StringBuilder();
        sb.AppendLine("# Rapport d'activité quotidien");
        sb.AppendLine($"Période: {from:dd/MM/yyyy} - {to:dd/MM/yyyy}");
        sb.AppendLine();
        sb.AppendLine("## Statistiques");
        sb.AppendLine($"- Total événements: {events.Count}");
        sb.AppendLine($"- Commandes: {events.Count(e => e.EventName == "command")}");
        sb.AppendLine($"- Appels outils: {events.Count(e => e.EventName == "tool_call")}");
        sb.AppendLine($"- Erreurs: {events.Count(e => e.EventName == "error")}");
        sb.AppendLine();
        sb.AppendLine("## Top commandes");
        foreach (var cmd in topCommands)
            sb.AppendLine($"- {cmd.Name}: {cmd.Count} fois");
        sb.AppendLine();
        sb.AppendLine("## Top outils");
        foreach (var tool in topTools)
            sb.AppendLine($"- {tool.Name}: {tool.Count} fois");

        return sb.ToString();
    }

    private string GenerateWeeklySummaryReport(DateTime from, DateTime to)
    {
        var summary = _analytics.GetSummary((to - from).Days);
        var dailyActivity = _analytics.GetDailyActivity((to - from).Days);

        var sb = new StringBuilder();
        sb.AppendLine("# Résumé hebdomadaire");
        sb.AppendLine($"Période: {from:dd/MM/yyyy} - {to:dd/MM/yyyy}");
        sb.AppendLine();
        sb.AppendLine("## Vue d'ensemble");
        sb.AppendLine($"- Jours actifs: {summary.UniqueDays}");
        sb.AppendLine($"- Total commandes: {summary.TotalCommands}");
        sb.AppendLine($"- Total outils: {summary.TotalToolCalls}");
        sb.AppendLine($"- Erreurs: {summary.TotalErrors}");
        sb.AppendLine();
        sb.AppendLine("## Activité par jour");
        foreach (var day in dailyActivity)
            sb.AppendLine($"- {day.Date:dd/MM}: {day.EventCount} événements");

        return sb.ToString();
    }

    private string GenerateCostAnalysisReport(DateTime from, DateTime to)
    {
        var summary = _costTracking.GetSummary((to - from).Days);
        var modelBreakdown = _costTracking.GetModelBreakdown((to - from).Days);
        var budgetStatus = _costTracking.GetBudgetStatus();

        var sb = new StringBuilder();
        sb.AppendLine("# Analyse des coûts");
        sb.AppendLine($"Période: {from:dd/MM/yyyy} - {to:dd/MM/yyyy}");
        sb.AppendLine();
        sb.AppendLine("## Résumé");
        sb.AppendLine($"- Coût total: {summary.TotalCost:C}");
        sb.AppendLine($"- Tokens input: {summary.TotalInputTokens:N0}");
        sb.AppendLine($"- Tokens output: {summary.TotalOutputTokens:N0}");
        sb.AppendLine($"- Coût moyen/jour: {summary.AverageCostPerDay:C}");
        sb.AppendLine();
        sb.AppendLine("## Par modèle");
        foreach (var model in modelBreakdown)
            sb.AppendLine($"- {model.Model}: {model.TotalCost:C} ({model.RequestCount} requêtes)");
        sb.AppendLine();
        sb.AppendLine("## Budget");
        sb.AppendLine($"- Budget mensuel: {budgetStatus.MonthlyBudget:C}");
        sb.AppendLine($"- Dépensé: {budgetStatus.SpentThisMonth:C} ({budgetStatus.PercentageUsed:F1}%)");
        sb.AppendLine($"- Restant: {budgetStatus.Remaining:C}");

        return sb.ToString();
    }

    private string GenerateToolUsageReport(DateTime from, DateTime to)
    {
        var topTools = _analytics.GetTopTools(20);

        var sb = new StringBuilder();
        sb.AppendLine("# Rapport d'utilisation des outils");
        sb.AppendLine($"Période: {from:dd/MM/yyyy} - {to:dd/MM/yyyy}");
        sb.AppendLine();
        sb.AppendLine("## Top outils");
        foreach (var tool in topTools)
            sb.AppendLine($"- {tool.Name}: {tool.Count} appels");

        return sb.ToString();
    }

    private string GenerateSystemHealthReport()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var gc = GC.GetTotalMemory(false);

        var sb = new StringBuilder();
        sb.AppendLine("# Rapport de santé système");
        sb.AppendLine($"Généré le: {DateTime.UtcNow:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("## Jarvis AI");
        sb.AppendLine($"- Mémoire utilisée: {gc / 1024 / 1024:N1} MB");
        sb.AppendLine($"- Threads: {process.Threads.Count}");
        sb.AppendLine($"- Uptime: {DateTime.UtcNow - process.StartTime:hh\\:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine("## Système");
        sb.AppendLine($"- CPU: {Environment.ProcessorCount} cœurs");
        sb.AppendLine($"- OS: {Environment.OSVersion}");

        return sb.ToString();
    }

    private DateTime CalculateNextRun(ReportFrequency frequency)
    {
        var now = DateTime.UtcNow;
        return frequency switch
        {
            ReportFrequency.Daily => now.AddDays(1).Date.AddHours(9),
            ReportFrequency.Weekly => now.AddDays(7 - (int)now.DayOfWeek).Date.AddHours(9),
            ReportFrequency.Monthly => new DateTime(now.Year, now.Month + 1, 1, 9, 0, 0, DateTimeKind.Utc),
            _ => now.AddDays(1)
        };
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<ReportSchedule>>(json);
                if (loaded is not null) _schedules.AddRange(loaded);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_schedules, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class ReportResult
{
    public ReportType Type { get; set; }
    public string Content { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
}

public sealed class ReportSchedule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ReportType Type { get; set; }
    public ReportFrequency Frequency { get; set; }
    public string? Recipient { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextRun { get; set; }
}

public enum ReportType { DailyActivity, WeeklySummary, CostAnalysis, ToolUsage, SystemHealth }
public enum ReportFrequency { Daily, Weekly, Monthly }
