namespace JarvisAI.Application.Reminders;

public sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Text { get; set; } = string.Empty;
    public DateTime DueAt { get; set; }
    public bool Completed { get; set; }
    public bool Notified { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
