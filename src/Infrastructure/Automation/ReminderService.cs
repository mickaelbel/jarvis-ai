using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Automation;

public interface IAutomationReminderService
{
    Task<AutomationReminder> CreateReminderAsync(string text, DateTime? remindAt = null, string? recurrence = null, CancellationToken ct = default);
    Task<List<AutomationReminder>> GetActiveRemindersAsync();
    Task<List<AutomationReminder>> GetCompletedRemindersAsync();
    Task<bool> CompleteReminderAsync(string id, CancellationToken ct = default);
    Task<bool> DeleteReminderAsync(string id, CancellationToken ct = default);
    Task<AutomationReminder?> ParseAndCreateReminderAsync(string userMessage, CancellationToken ct = default);
}

public sealed class AutomationReminderService : IAutomationReminderService
{
    private readonly ILogger<AutomationReminderService> _logger;
    private readonly string _storagePath;
    private readonly List<AutomationReminder> _reminders = new();
    private readonly System.Threading.Timer _checkTimer;

    public AutomationReminderService(ILogger<AutomationReminderService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "reminders.json");
        LoadReminders();

        // Check every 30 seconds
        _checkTimer = new System.Threading.Timer(CheckReminders, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public async Task<AutomationReminder> CreateReminderAsync(string text, DateTime? remindAt = null, string? recurrence = null, CancellationToken ct = default)
    {
        var reminder = new AutomationReminder
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Text = text,
            RemindAt = remindAt ?? DateTime.Now.AddHours(1),
            Recurrence = recurrence,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        _reminders.Add(reminder);
        SaveReminders();

        _logger.LogInformation("[Reminder] Created: {Text} at {Time}", text, reminder.RemindAt);
        return reminder;
    }

    public Task<List<AutomationReminder>> GetActiveRemindersAsync()
    {
        var active = _reminders
            .Where(r => r.IsActive && r.RemindAt > DateTime.Now)
            .OrderBy(r => r.RemindAt)
            .ToList();
        return Task.FromResult(active);
    }

    public Task<List<AutomationReminder>> GetCompletedRemindersAsync()
    {
        var completed = _reminders
            .Where(r => !r.IsActive)
            .OrderByDescending(r => r.CompletedAt)
            .ToList();
        return Task.FromResult(completed);
    }

    public Task<bool> CompleteReminderAsync(string id, CancellationToken ct = default)
    {
        var reminder = _reminders.FirstOrDefault(r => r.Id == id);
        if (reminder is null) return Task.FromResult(false);

        reminder.IsActive = false;
        reminder.CompletedAt = DateTime.UtcNow;
        SaveReminders();

        _logger.LogInformation("[Reminder] Completed: {Text}", reminder.Text);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteReminderAsync(string id, CancellationToken ct = default)
    {
        var removed = _reminders.RemoveAll(r => r.Id == id) > 0;
        if (removed) SaveReminders();
        return Task.FromResult(removed);
    }

    public async Task<AutomationReminder?> ParseAndCreateReminderAsync(string userMessage, CancellationToken ct = default)
    {
        // Simple pattern matching for common reminder patterns
        var lower = userMessage.ToLowerInvariant();

        // "rappelle-moi de..." or "n'oublie pas de..."
        if (!lower.Contains("rappelle") && !lower.Contains("n'oublie") && !lower.Contains("reminder"))
            return null;

        // Extract time
        DateTime? remindAt = null;
        var timePatterns = new[]
        {
            ("dans 5 minutes", TimeSpan.FromMinutes(5)),
            ("dans 10 minutes", TimeSpan.FromMinutes(10)),
            ("dans 15 minutes", TimeSpan.FromMinutes(15)),
            ("dans 30 minutes", TimeSpan.FromMinutes(30)),
            ("dans 1 heure", TimeSpan.FromHours(1)),
            ("dans 2 heures", TimeSpan.FromHours(2)),
            ("dans 3 heures", TimeSpan.FromHours(3)),
            ("demain matin", TimeSpan.FromHours(24 - DateTime.Now.Hour + 9)),
            ("demain après-midi", TimeSpan.FromHours(24 - DateTime.Now.Hour + 14)),
            ("lundi", GetNextDay(DayOfWeek.Monday)),
            ("mardi", GetNextDay(DayOfWeek.Tuesday)),
            ("mercredi", GetNextDay(DayOfWeek.Wednesday)),
            ("jeudi", GetNextDay(DayOfWeek.Thursday)),
            ("vendredi", GetNextDay(DayOfWeek.Friday)),
        };

        foreach (var (pattern, offset) in timePatterns)
        {
            if (lower.Contains(pattern))
            {
                if (offset is TimeSpan ts)
                    remindAt = DateTime.Now.Add(ts);
                else if (offset is TimeSpan days)
                    remindAt = DateTime.Now.Add(days);
                break;
            }
        }

        // Extract text (everything after "de" or "pour")
        var text = userMessage;
        var deIndex = lower.IndexOf(" de ");
        if (deIndex > 0)
            text = userMessage[(deIndex + 4)..];
        else
        {
            var pourIndex = lower.IndexOf(" pour ");
            if (pourIndex > 0)
                text = userMessage[(pourIndex + 6)..];
        }

        text = text.TrimEnd('.', '!', '?').Trim();
        if (string.IsNullOrWhiteSpace(text))
            text = "Rappel";

        return await CreateReminderAsync(text, remindAt, null, ct);
    }

    private void CheckReminders(object? state)
    {
        var now = DateTime.Now;
        var dueReminders = _reminders
            .Where(r => r.IsActive && r.RemindAt <= now)
            .ToList();

        foreach (var reminder in dueReminders)
        {
            _logger.LogInformation("[Reminder] DUE: {Text}", reminder.Text);

            // Show notification
            ShowNotification("Rappel", reminder.Text);

            // Handle recurrence
            if (!string.IsNullOrEmpty(reminder.Recurrence))
            {
                reminder.RemindAt = GetNextOccurrence(reminder.Recurrence);
            }
            else
            {
                reminder.IsActive = false;
                reminder.CompletedAt = now;
            }
        }

        if (dueReminders.Count > 0)
            SaveReminders();
    }

    private void ShowNotification(string title, string message)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"[System.Windows.MessageBox]::Show('{message}', '{title}')\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private static DateTime GetNextOccurrence(string recurrence)
    {
        var now = DateTime.Now;
        return recurrence.ToLowerInvariant() switch
        {
            "daily" or "quotidien" => now.AddDays(1),
            "weekly" or "hebdomadaire" => now.AddDays(7),
            "monthly" or "mensuel" => now.AddMonths(1),
            _ => now.AddHours(1)
        };
    }

    private static TimeSpan GetNextDay(DayOfWeek day)
    {
        var now = DateTime.Now;
        var diff = day - now.DayOfWeek;
        if (diff <= 0) diff += 7;
        return TimeSpan.FromDays(diff);
    }

    private void LoadReminders()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<AutomationReminder>>(json);
                if (loaded is not null)
                    _reminders.AddRange(loaded);
            }
        }
        catch { }
    }

    private void SaveReminders()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_reminders, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class AutomationReminder
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime RemindAt { get; set; }
    public string? Recurrence { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
