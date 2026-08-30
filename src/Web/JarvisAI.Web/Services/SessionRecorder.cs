using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ISessionRecorder
{
    bool IsRecording { get; }
    void StartRecording(string? sessionName = null);
    SessionRecord StopRecording();
    void RecordAction(RecordedAction action);
    Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(int maxCount = 20, CancellationToken ct = default);
    Task ReplaySessionAsync(string sessionId, Func<RecordedAction, Task> actionHandler, CancellationToken ct = default);
}

public sealed class SessionRecorder : ISessionRecorder
{
    private readonly ILogger<SessionRecorder> _logger;
    private readonly string _storagePath;
    private readonly List<SessionRecord> _sessions = new();
    private SessionRecord? _current;

    public bool IsRecording => _current is not null;

    public SessionRecorder(ILogger<SessionRecorder> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "session_records.json");
        Load();
    }

    public void StartRecording(string? sessionName = null)
    {
        _current = new SessionRecord
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = sessionName ?? $"Session {DateTime.Now:yyyy-MM-dd HH:mm}",
            StartedAt = DateTime.UtcNow
        };
        _logger.LogInformation("[Recorder] Recording started: {Name}", _current.Name);
    }

    public SessionRecord StopRecording()
    {
        if (_current is null)
            return new SessionRecord { Name = "(not recording)" };

        _current.EndedAt = DateTime.UtcNow;
        _current.Duration = _current.EndedAt.Value - _current.StartedAt;
        _sessions.Add(_current);
        Save();

        var record = _current;
        _current = null;

        _logger.LogInformation("[Recorder] Recording stopped: {Name} ({Count} actions, {Duration})",
            record.Name, record.Actions.Count, record.Duration);
        return record;
    }

    public void RecordAction(RecordedAction action)
    {
        if (_current is null) return;

        action.Timestamp = DateTime.UtcNow;
        action.Offset = action.Timestamp - _current.StartedAt;
        _current.Actions.Add(action);
    }

    public async Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(int maxCount = 20, CancellationToken ct = default)
    {
        return await Task.FromResult(_sessions
            .OrderByDescending(s => s.StartedAt)
            .Take(maxCount)
            .ToList());
    }

    public async Task ReplaySessionAsync(string sessionId, Func<RecordedAction, Task> actionHandler, CancellationToken ct = default)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;

        _logger.LogInformation("[Recorder] Replaying {Name} ({Count} actions)",
            session.Name, session.Actions.Count);

        var prevOffset = TimeSpan.Zero;
        foreach (var action in session.Actions.OrderBy(a => a.Offset))
        {
            if (ct.IsCancellationRequested) break;

            var delay = action.Offset - prevOffset;
            if (delay > TimeSpan.Zero && delay.TotalSeconds < 60)
                await Task.Delay(delay, ct);

            await actionHandler(action);
            prevOffset = action.Offset;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<SessionRecord>>(json);
                if (loaded is not null) _sessions.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public List<RecordedAction> Actions { get; set; } = new();
}

public sealed class RecordedAction
{
    public string Type { get; set; } = "";
    public string? ToolName { get; set; }
    public string? Description { get; set; }
    public string? Input { get; set; }
    public string? Output { get; set; }
    public bool Success { get; set; } = true;
    public DateTime Timestamp { get; set; }
    public TimeSpan Offset { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
