namespace JarvisAI.Application.Reminders;

public interface IReminderService
{
    Reminder Add(string text, DateTime dueAt);
    bool Cancel(string id);
    void MarkNotified(string id);
    IReadOnlyList<Reminder> GetActive();
    IReadOnlyList<Reminder> GetDue(DateTime now);
    IReadOnlyList<Reminder> GetAll();
}
