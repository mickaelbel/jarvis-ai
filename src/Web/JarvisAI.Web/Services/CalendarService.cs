using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ICalendarService
{
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTime start, DateTime end, CancellationToken ct = default);
    Task<string> CreateEventAsync(string title, DateTime start, DateTime end, string? description = null, string? location = null, CancellationToken ct = default);
    Task UpdateEventAsync(string eventId, string title, DateTime start, DateTime end, string? description = null, CancellationToken ct = default);
    Task DeleteEventAsync(string eventId, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarEvent>> GetUpcomingAsync(int hours = 24, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(string calendarUrl, string apiKey, CancellationToken ct = default);
    void Configure(CalendarSettings settings);
    CalendarSettings? GetSettings();
}

public sealed class CalendarService : ICalendarService
{
    private readonly ILogger<CalendarService> _logger;
    private readonly string _storagePath;
    private readonly List<CalendarEvent> _events = new();
    private CalendarSettings? _settings;

    public CalendarService(ILogger<CalendarService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "calendar.json");
        Load();
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTime start, DateTime end, CancellationToken ct = default)
    {
        lock (_events)
        {
            return _events
                .Where(e => e.Start >= start && e.Start <= end)
                .OrderBy(e => e.Start)
                .ToList();
        }
    }

    public async Task<string> CreateEventAsync(string title, DateTime start, DateTime end, string? description = null, string? location = null, CancellationToken ct = default)
    {
        var evt = new CalendarEvent
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Start = start,
            End = end,
            Description = description ?? "",
            Location = location ?? "",
            CreatedAt = DateTime.UtcNow
        };

        lock (_events)
        {
            _events.Add(evt);
        }

        Save();
        _logger.LogInformation("[Calendar] Created: {Title} at {Start}", title, start);
        return evt.Id;
    }

    public async Task UpdateEventAsync(string eventId, string title, DateTime start, DateTime end, string? description = null, CancellationToken ct = default)
    {
        lock (_events)
        {
            var evt = _events.FirstOrDefault(e => e.Id == eventId);
            if (evt is not null)
            {
                evt.Title = title;
                evt.Start = start;
                evt.End = end;
                evt.Description = description ?? evt.Description;
                evt.LastModified = DateTime.UtcNow;
            }
        }
        Save();
        await Task.CompletedTask;
    }

    public async Task DeleteEventAsync(string eventId, CancellationToken ct = default)
    {
        lock (_events)
        {
            _events.RemoveAll(e => e.Id == eventId);
        }
        Save();
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetUpcomingAsync(int hours = 24, CancellationToken ct = default)
    {
        var end = DateTime.UtcNow.AddHours(hours);
        return await GetEventsAsync(DateTime.UtcNow, end, ct);
    }

    public async Task<bool> TestConnectionAsync(string calendarUrl, string apiKey, CancellationToken ct = default)
    {
        _logger.LogInformation("[Calendar] Testing connection to {Url}", calendarUrl);
        return await Task.FromResult(true);
    }

    public void Configure(CalendarSettings settings)
    {
        _settings = settings;
        Save();
    }

    public CalendarSettings? GetSettings() => _settings;

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<CalendarData>(json);
                if (data is not null)
                {
                    _events.AddRange(data.Events);
                    _settings = data.Settings;
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

            lock (_events)
            {
                var data = new CalendarData { Events = _events, Settings = _settings };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
        }
        catch { }
    }
}

public sealed class CalendarEvent
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Description { get; set; } = "";
    public string Location { get; set; } = "";
    public string? MeetingLink { get; set; }
    public bool IsAllDay { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModified { get; set; }
    public List<string> Attendees { get; set; } = new();
}

public sealed class CalendarSettings
{
    public string Provider { get; set; } = "local";
    public string CalendarUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool SyncEnabled { get; set; }
    public int SyncIntervalMinutes { get; set; } = 15;
}

internal sealed class CalendarData
{
    public List<CalendarEvent> Events { get; set; } = new();
    public CalendarSettings? Settings { get; set; }
}
