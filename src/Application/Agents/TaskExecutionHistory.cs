namespace JarvisAI.Application.Agents;

public sealed class TaskExecutionStep
{
    public string StageName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Detail { get; init; }

    public static TaskExecutionStep System(string description, bool success = true, string? detail = null) =>
        new() { StageName = "System", Description = description, Timestamp = DateTimeOffset.UtcNow, Success = success, Detail = detail };

    public static TaskExecutionStep Thought(string description) =>
        new() { StageName = "Thought", Description = description, Timestamp = DateTimeOffset.UtcNow, Success = true };

    public static TaskExecutionStep Tool(string toolName, string description, long durationMs, bool success, string? detail = null) =>
        new() { StageName = "Tool", Description = $"[{toolName}] {description}", Timestamp = DateTimeOffset.UtcNow, DurationMs = durationMs, Success = success, Detail = detail };

    public static TaskExecutionStep Final(string description, long durationMs) =>
        new() { StageName = "Final", Description = description, Timestamp = DateTimeOffset.UtcNow, DurationMs = durationMs, Success = true };

    public static TaskExecutionStep Error(string message, string? detail = null) =>
        new() { StageName = "Error", Description = message, Timestamp = DateTimeOffset.UtcNow, Success = false, Detail = detail };
}

public sealed record TaskExecutionRecord
{
    public Guid RecordId { get; init; } = Guid.NewGuid();
    public Guid? CorrelationId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public long TotalDurationMs { get; init; }
    public bool Success { get; init; }
    public required string UserMessage { get; init; }
    public string? FinalResponse { get; init; }
    public string? ModelUsed { get; init; }
    public IReadOnlyList<TaskExecutionStep> Steps { get; init; } = Array.Empty<TaskExecutionStep>();
    public int ToolCallCount { get; init; }
    public int RoundCount { get; init; }
    public string? ErrorMessage { get; init; }
}

public interface ITaskExecutionHistory
{
    event EventHandler? Changed;
    void StartRecording(string userMessage, Guid? correlationId);
    void AddStep(TaskExecutionStep step);
    TaskExecutionRecord Complete(string? finalResponse, bool success, string? errorMessage = null);
    TaskExecutionRecord? GetActive();
    TaskExecutionRecord? GetHistory(Guid correlationId);
    IReadOnlyList<TaskExecutionRecord> GetRecentHistory(int count = 20);
    int CleanupExpired(TimeSpan retention);
}

public sealed class InMemoryTaskExecutionHistory : ITaskExecutionHistory
{
    private readonly Dictionary<Guid, TaskExecutionRecord> _records = new();
    private readonly List<Guid> _recentKeys = new();
    private readonly List<TaskExecutionStep> _activeSteps = new();
    private readonly object _lock = new();

    private Guid? _activeCorrelationId;
    private string? _activeUserMessage;
    private DateTimeOffset _activeStartTime;

    public event EventHandler? Changed;

    public void StartRecording(string userMessage, Guid? correlationId)
    {
        lock (_lock)
        {
            _activeCorrelationId = correlationId;
            _activeUserMessage = userMessage;
            _activeStartTime = DateTimeOffset.UtcNow;
            _activeSteps.Clear();
        }
        RaiseChanged();
    }

    public void AddStep(TaskExecutionStep step)
    {
        lock (_lock)
        {
            _activeSteps.Add(step);
        }
        RaiseChanged();
    }

    public TaskExecutionRecord Complete(string? finalResponse, bool success, string? errorMessage = null)
    {
        TaskExecutionRecord record;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            record = new TaskExecutionRecord
            {
                RecordId = Guid.NewGuid(),
                CorrelationId = _activeCorrelationId,
                CreatedAt = _activeStartTime,
                CompletedAt = now,
                TotalDurationMs = (long)(now - _activeStartTime).TotalMilliseconds,
                Success = success,
                UserMessage = _activeUserMessage ?? string.Empty,
                FinalResponse = finalResponse,
                Steps = _activeSteps.ToList().AsReadOnly(),
                ToolCallCount = _activeSteps.Count(s => s.StageName == "Tool"),
                RoundCount = _activeSteps.Count(s => s.StageName == "Tool"),
                ErrorMessage = errorMessage,
            };

            var key = _activeCorrelationId ?? Guid.NewGuid();
            _records[key] = record;
            _recentKeys.Add(key);

            if (_recentKeys.Count > 200)
                _recentKeys.RemoveAt(0);

            _activeCorrelationId = null;
            _activeUserMessage = null;
            _activeSteps.Clear();
        }

        RaiseChanged();
        return record;
    }

    public TaskExecutionRecord? GetActive()
    {
        lock (_lock)
        {
            if (_activeCorrelationId is null)
                return null;

            var now = DateTimeOffset.UtcNow;
            return new TaskExecutionRecord
            {
                RecordId = Guid.NewGuid(),
                CorrelationId = _activeCorrelationId,
                CreatedAt = _activeStartTime,
                TotalDurationMs = (long)(now - _activeStartTime).TotalMilliseconds,
                UserMessage = _activeUserMessage ?? string.Empty,
                Steps = _activeSteps.ToList().AsReadOnly(),
                ToolCallCount = _activeSteps.Count(s => s.StageName == "Tool"),
                RoundCount = _activeSteps.Count(s => s.StageName == "Tool"),
            };
        }
    }

    public TaskExecutionRecord? GetHistory(Guid correlationId)
    {
        lock (_lock)
        {
            return _records.TryGetValue(correlationId, out var record) ? record : null;
        }
    }

    public IReadOnlyList<TaskExecutionRecord> GetRecentHistory(int count = 20)
    {
        lock (_lock)
        {
            return _recentKeys
                .TakeLast(count)
                .Select(k => _records[k])
                .ToList()
                .AsReadOnly();
        }
    }

    public int CleanupExpired(TimeSpan retention)
    {
        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow - retention;
            var toRemove = _records
                .Where(kv => kv.Value.CompletedAt.HasValue && kv.Value.CompletedAt.Value < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _records.Remove(key);
                _recentKeys.Remove(key);
            }

            return toRemove.Count;
        }
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}