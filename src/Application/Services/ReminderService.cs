using System.Text.Json;

namespace JarvisAI.Application.Services;

public sealed record Reminder(
    string Id,
    string Title,
    DateTime DueUtc,
    string? Message = null,
    bool Fired = false);

public interface IReminderService
{
    IReadOnlyList<Reminder> GetAll();
    Reminder Add(string title, DateTime dueUtc, string? message = null);
    bool Remove(string id);
    List<Reminder> GetDueNow();
    void MarkFired(string id);
}

public sealed class ReminderService : IReminderService
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "reminders.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private List<Reminder> _items = new();

    public ReminderService() => Load();

    public IReadOnlyList<Reminder> GetAll() { lock (_lock) return _items.ToList(); }

    public Reminder Add(string title, DateTime dueUtc, string? message = null)
    {
        var reminder = new Reminder(Guid.NewGuid().ToString("N")[..8], title, dueUtc, message);
        lock (_lock) { _items.Add(reminder); Save(); }
        return reminder;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_lock) { removed = _items.RemoveAll(r => r.Id == id) > 0; if (removed) Save(); }
        return removed;
    }

    public List<Reminder> GetDueNow()
    {
        var now = DateTime.UtcNow;
        lock (_lock) return _items.Where(r => !r.Fired && r.DueUtc <= now).ToList();
    }

    public void MarkFired(string id)
    {
        lock (_lock)
        {
            var idx = _items.FindIndex(r => r.Id == id);
            if (idx >= 0) _items[idx] = _items[idx] with { Fired = true };
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            _items = JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(StorePath), JsonOpts) ?? new();
        }
        catch { _items = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_items, JsonOpts));
        }
        catch { }
    }
}
