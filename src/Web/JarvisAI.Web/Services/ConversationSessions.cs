using System.Text.Json;

namespace JarvisAI.Web.Services;

/// <summary>
/// A ChatGPT-style conversation grouped into a titled session. Chat and voice
/// exchanges both live in the same store, distinguished by <see cref="Source"/>.
/// </summary>
public sealed class ConversationSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = "Nouvelle conversation";
    public string Source { get; set; } = "chat";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string Model { get; set; } = "";
    public List<SessionMessage> Messages { get; set; } = new();
    public float[]? TitleEmbedding { get; set; }
}

public sealed class SessionMessage
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Model { get; set; } = "";
}

/// <summary>
/// JSON-persisted store of conversation sessions (one file: conversation-sessions.json).
/// Thread-safe: all mutations happen under a lock.
/// </summary>
public sealed class ConversationSessionStore
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly List<ConversationSession> _sessions = new();
    private const int MaxSessions = 200;

    public event Action? Changed;

    public ConversationSessionStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "conversation-sessions.json");
        Load();
    }

    public IReadOnlyList<ConversationSession> GetAll()
    {
        lock (_lock) return _sessions.ToList();
    }

    public ConversationSession? GetById(string id)
    {
        lock (_lock) return _sessions.FirstOrDefault(s => s.Id == id);
    }

    public ConversationSession Create(string source)
    {
        ConversationSession session;
        lock (_lock)
        {
            session = new ConversationSession { Source = source };
            _sessions.Add(session);
            Trim();
            Save();
        }
        Changed?.Invoke();
        return session;
    }

    public void Update(string id, Action<ConversationSession> mutate)
    {
        lock (_lock)
        {
            var session = _sessions.FirstOrDefault(s => s.Id == id);
            if (session is null) return;
            mutate(session);
            session.UpdatedAt = DateTime.UtcNow;
            Save();
        }
        Changed?.Invoke();
    }

    public bool Delete(string id)
    {
        var removed = false;
        lock (_lock)
        {
            removed = _sessions.RemoveAll(s => s.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Clear(string source)
    {
        lock (_lock)
        {
            _sessions.RemoveAll(s => s.Source == source);
            Save();
        }
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _sessions.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private void Trim()
    {
        while (_sessions.Count > MaxSessions)
            _sessions.RemoveAt(0);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var items = JsonSerializer.Deserialize<List<ConversationSession>>(File.ReadAllText(_filePath));
                if (items is not null) _sessions.AddRange(items);
            }
        }
        catch
        {
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_sessions));
        }
        catch
        {
        }
    }
}
