using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ICostTrackingService
{
    void TrackUsage(string model, int inputTokens, int outputTokens, double costPer1kInput, double costPer1kOutput);
    CostSummary GetSummary(int days = 30);
    IReadOnlyList<DailyCost> GetDailyCosts(int days = 30);
    IReadOnlyList<ModelCost> GetModelBreakdown(int days = 30);
    void SetBudget(decimal monthlyBudget);
    BudgetStatus GetBudgetStatus();
}

public sealed class CostTrackingService : ICostTrackingService
{
    private readonly ILogger<CostTrackingService> _logger;
    private readonly string _storagePath;
    private readonly List<UsageRecord> _records = new();
    private decimal _monthlyBudget = 50.0m;

    public CostTrackingService(ILogger<CostTrackingService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "cost_tracking.json");
        Load();
    }

    public void TrackUsage(string model, int inputTokens, int outputTokens, double costPer1kInput, double costPer1kOutput)
    {
        var inputCost = (inputTokens / 1000.0) * costPer1kInput;
        var outputCost = (outputTokens / 1000.0) * costPer1kOutput;
        var totalCost = inputCost + outputCost;

        var record = new UsageRecord
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            InputCost = inputCost,
            OutputCost = outputCost,
            TotalCost = totalCost,
            Timestamp = DateTime.UtcNow
        };

        _records.Add(record);

        // Keep only last 30 days
        var cutoff = DateTime.UtcNow.AddDays(-30);
        _records.RemoveAll(r => r.Timestamp < cutoff);

        Save();
        _logger.LogDebug("[CostTrack] {Model}: {Cost:C4} ({In}+{Out} tokens)", model, totalCost, inputTokens, outputTokens);
    }

    public CostSummary GetSummary(int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var recent = _records.Where(r => r.Timestamp >= cutoff).ToList();

        return new CostSummary
        {
            TotalCost = recent.Sum(r => r.TotalCost),
            TotalInputTokens = recent.Sum(r => r.InputTokens),
            TotalOutputTokens = recent.Sum(r => r.OutputTokens),
            AverageCostPerDay = recent.Any() ? recent.Sum(r => r.TotalCost) / days : 0,
            PeriodDays = days,
            RecordCount = recent.Count
        };
    }

    public IReadOnlyList<DailyCost> GetDailyCosts(int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return _records
            .Where(r => r.Timestamp >= cutoff)
            .GroupBy(r => r.Timestamp.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyCost
            {
                Date = g.Key,
                Cost = g.Sum(r => r.TotalCost),
                Tokens = g.Sum(r => r.InputTokens + r.OutputTokens)
            })
            .ToList();
    }

    public IReadOnlyList<ModelCost> GetModelBreakdown(int days = 30)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return _records
            .Where(r => r.Timestamp >= cutoff)
            .GroupBy(r => r.Model)
            .OrderByDescending(g => g.Sum(r => r.TotalCost))
            .Select(g => new ModelCost
            {
                Model = g.Key,
                TotalCost = g.Sum(r => r.TotalCost),
                RequestCount = g.Count(),
                AverageTokensPerRequest = g.Average(r => r.InputTokens + r.OutputTokens)
            })
            .ToList();
    }

    public void SetBudget(decimal monthlyBudget)
    {
        _monthlyBudget = monthlyBudget;
        Save();
    }

    public BudgetStatus GetBudgetStatus()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthRecords = _records.Where(r => r.Timestamp >= monthStart).ToList();
        var spent = monthRecords.Sum(r => (decimal)r.TotalCost);

        return new BudgetStatus
        {
            MonthlyBudget = _monthlyBudget,
            SpentThisMonth = spent,
            Remaining = _monthlyBudget - spent,
            PercentageUsed = _monthlyBudget > 0 ? (double)(spent / _monthlyBudget) * 100 : 0,
            DaysRemainingInMonth = DateTime.DaysInMonth(now.Year, now.Month) - now.Day,
            ProjectedMonthlySpend = now.Day > 0 ? spent / now.Day * DateTime.DaysInMonth(now.Year, now.Month) : 0
        };
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("records", out var recordsEl))
                {
                    var recordsJson = recordsEl.GetRawText();
                    var loaded = JsonSerializer.Deserialize<List<UsageRecord>>(recordsJson);
                    if (loaded is not null) _records.AddRange(loaded);
                }

                if (root.TryGetProperty("budget", out var budgetEl))
                {
                    _monthlyBudget = budgetEl.GetDecimal();
                }
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

            var data = new { records = _records, budget = _monthlyBudget };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class UsageRecord
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double InputCost { get; set; }
    public double OutputCost { get; set; }
    public double TotalCost { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class CostSummary
{
    public double TotalCost { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public double AverageCostPerDay { get; set; }
    public int PeriodDays { get; set; }
    public int RecordCount { get; set; }
}

public sealed class DailyCost
{
    public DateTime Date { get; set; }
    public double Cost { get; set; }
    public int Tokens { get; set; }
}

public sealed class ModelCost
{
    public string Model { get; set; } = "";
    public double TotalCost { get; set; }
    public int RequestCount { get; set; }
    public double AverageTokensPerRequest { get; set; }
}

public sealed class BudgetStatus
{
    public decimal MonthlyBudget { get; set; }
    public decimal SpentThisMonth { get; set; }
    public decimal Remaining { get; set; }
    public double PercentageUsed { get; set; }
    public int DaysRemainingInMonth { get; set; }
    public decimal ProjectedMonthlySpend { get; set; }
}
