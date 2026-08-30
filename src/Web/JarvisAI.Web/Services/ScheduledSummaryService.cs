using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IScheduledSummaryService
{
    Task<IReadOnlyList<SummarySchedule>> GetSchedulesAsync(CancellationToken ct = default);
    Task<string> CreateScheduleAsync(string name, SummaryFrequency frequency, string? channel = null, CancellationToken ct = default);
    Task DeleteScheduleAsync(string scheduleId, CancellationToken ct = default);
    Task EnableScheduleAsync(string scheduleId, CancellationToken ct = default);
    Task DisableScheduleAsync(string scheduleId, CancellationToken ct = default);
    Task<string> GenerateSummaryAsync(SummaryFrequency frequency, CancellationToken ct = default);
}

public sealed class ScheduledSummaryService : IScheduledSummaryService
{
    private readonly ILogger<ScheduledSummaryService> _logger;
    private readonly string _storagePath;
    private readonly List<SummarySchedule> _schedules = new();

    public ScheduledSummaryService(ILogger<ScheduledSummaryService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "scheduled_summaries.json");
        Load();
    }

    public async Task<IReadOnlyList<SummarySchedule>> GetSchedulesAsync(CancellationToken ct = default)
    {
        return await Task.FromResult(_schedules.ToList());
    }

    public async Task<string> CreateScheduleAsync(string name, SummaryFrequency frequency, string? channel = null, CancellationToken ct = default)
    {
        var schedule = new SummarySchedule
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Frequency = frequency,
            Channel = channel ?? "chat",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            LastRun = null
        };

        _schedules.Add(schedule);
        Save();
        _logger.LogInformation("[Summary] Schedule created: {Name} ({Frequency})", name, frequency);
        return schedule.Id;
    }

    public async Task DeleteScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        _schedules.RemoveAll(s => s.Id == scheduleId);
        Save();
        await Task.CompletedTask;
    }

    public async Task EnableScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        var schedule = _schedules.FirstOrDefault(s => s.Id == scheduleId);
        if (schedule is not null)
        {
            schedule.IsEnabled = true;
            Save();
        }
        await Task.CompletedTask;
    }

    public async Task DisableScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        var schedule = _schedules.FirstOrDefault(s => s.Id == scheduleId);
        if (schedule is not null)
        {
            schedule.IsEnabled = false;
            Save();
        }
        await Task.CompletedTask;
    }

    public async Task<string> GenerateSummaryAsync(SummaryFrequency frequency, CancellationToken ct = default)
    {
        var timeRange = frequency switch
        {
            SummaryFrequency.Daily => "les dernières 24h",
            SummaryFrequency.Weekly => "la dernière semaine",
            SummaryFrequency.Monthly => "le dernier mois",
            _ => "la dernière période"
        };

        var summary = $"## Résumé {frequency}\n\n" +
            $"**Période** : {timeRange}\n\n" +
            $"**Activité** :\n" +
            $"- Sessions actives\n" +
            $"- Outils utilisés\n" +
            $"- Mémoires créées\n\n" +
            $"*Généré le {DateTime.Now:dd/MM/yyyy à HH:mm}*";

        _logger.LogInformation("[Summary] Generated {Frequency} summary", frequency);
        return await Task.FromResult(summary);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<SummarySchedule>>(json);
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

public sealed class SummarySchedule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SummaryFrequency Frequency { get; set; }
    public string Channel { get; set; } = "chat";
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastRun { get; set; }
}

public enum SummaryFrequency { Daily, Weekly, Monthly }
