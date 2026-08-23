using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Reminders;

/// <summary>
/// Service de rappels : crée, annule et fournit les rappels arrivés à échéance.
/// Persistance JSON locale pour survivre aux redémarrages, thread-safe.
/// </summary>
public sealed class ReminderService : IReminderService
{
    private static readonly string StorePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "reminders.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly List<Reminder> _reminders;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(ILogger<ReminderService> logger)
    {
        _logger = logger;
        _reminders = Load();
    }

    public Reminder Add(string text, DateTime dueAt)
    {
        var reminder = new Reminder { Text = text.Trim(), DueAt = dueAt };
        if (string.IsNullOrWhiteSpace(reminder.Text)) return reminder;

        lock (_lock)
        {
            _reminders.Add(reminder);
            Persist();
        }
        _logger.LogInformation("[Reminders] Added reminder {Id} due {DueAt}: {Text}", reminder.Id, dueAt, text);
        return reminder;
    }

    public bool Cancel(string id)
    {
        lock (_lock)
        {
            var reminder = _reminders.FirstOrDefault(r => r.Id == id);
            if (reminder is null) return false;
            _reminders.Remove(reminder);
            Persist();
            _logger.LogInformation("[Reminders] Cancelled reminder {Id}", id);
            return true;
        }
    }

    public void MarkNotified(string id)
    {
        lock (_lock)
        {
            var reminder = _reminders.FirstOrDefault(r => r.Id == id);
            if (reminder is null) return;
            reminder.Notified = true;
            reminder.Completed = true;
            Persist();
        }
    }

    public IReadOnlyList<Reminder> GetActive()
    {
        lock (_lock)
            return _reminders.Where(r => !r.Completed && r.DueAt >= DateTime.Now).OrderBy(r => r.DueAt).ToList();
    }

    public IReadOnlyList<Reminder> GetDue(DateTime now)
    {
        lock (_lock)
            return _reminders.Where(r => !r.Completed && !r.Notified && r.DueAt <= now).OrderBy(r => r.DueAt).ToList();
    }

    public IReadOnlyList<Reminder> GetAll()
    {
        lock (_lock) return _reminders.ToList();
    }

    private void Persist()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_reminders, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Reminders] Failed to persist reminders");
        }
    }

    private static List<Reminder> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(StorePath)) ?? new List<Reminder>();
        }
        catch
        {
        }
        return new List<Reminder>();
    }
}
