using System.Collections.Concurrent;
using JarvisAI.Application.Planning;

namespace JarvisAI.Application.Agents;

public enum RunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    TimedOut
}

public enum RunStepKind
{
    Thought,
    Plan,
    ToolStarted,
    ToolCompleted,
    Observation,
    Final,
    Error,
    Memory
}

public sealed class RunStep
{
    public int Index { get; }
    public RunStepKind Kind { get; }
    public string Content { get; }
    public string? ToolName { get; }
    public bool? Success { get; }
    public TimeSpan? Duration { get; }
    public DateTimeOffset OccurredAt { get; }

    public RunStep(int index, RunStepKind kind, string content, string? toolName = null, bool? success = null, TimeSpan? duration = null, DateTimeOffset? occurredAt = null)
    {
        Index = index;
        Kind = kind;
        Content = content;
        ToolName = toolName;
        Success = success;
        Duration = duration;
        OccurredAt = occurredAt ?? DateTimeOffset.UtcNow;
    }
}

public sealed class RunRecord
{
    private readonly object _lock = new();
    private readonly List<RunStep> _steps = new();
    private readonly List<string> _reasoningTrace = new();
    private readonly List<string> _memoryNotes = new();
    private readonly List<string> _plugins = new();

    public Guid RunId { get; }
    public string Goal { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public RunStatus Status { get; private set; } = RunStatus.Running;
    public string? Model { get; private set; }
    public Plan? Plan { get; private set; }
    public string? FinalResponse { get; private set; }
    public string? Error { get; private set; }
    public string? Reason { get; private set; }
    public int Iterations { get; private set; }
    public int Retries { get; private set; }
    public int ToolsExecuted { get; private set; }
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }

    public RunRecord(Guid runId, string goal, DateTimeOffset startedAt)
    {
        RunId = runId;
        Goal = goal;
        StartedAt = startedAt;
    }

    public IReadOnlyList<RunStep> Steps
    {
        get { lock (_lock) return _steps.ToList(); }
    }

    public IReadOnlyList<string> ReasoningTrace
    {
        get { lock (_lock) return _reasoningTrace.ToList(); }
    }

    public IReadOnlyList<string> MemoryNotes
    {
        get { lock (_lock) return _memoryNotes.ToList(); }
    }

    public IReadOnlyList<string> Plugins
    {
        get { lock (_lock) return _plugins.ToList(); }
    }

    public TimeSpan Duration => (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public int AddStep(RunStepKind kind, string content, string? toolName = null, bool? success = null, TimeSpan? duration = null)
    {
        lock (_lock)
        {
            var step = new RunStep(_steps.Count, kind, content, toolName, success, duration);
            _steps.Add(step);
            return step.Index;
        }
    }

    public void AddReasoning(string line)
    {
        lock (_lock) _reasoningTrace.Add(line);
    }

    public void AddMemoryNote(string note)
    {
        lock (_lock) _memoryNotes.Add(note);
    }

    public void SetPlugins(IEnumerable<string> plugins)
    {
        lock (_lock)
        {
            _plugins.Clear();
            _plugins.AddRange(plugins);
        }
    }

    public void SetModel(string model)
    {
        lock (_lock) Model = model;
    }

    public void SetPlan(Plan plan)
    {
        lock (_lock) Plan = plan;
    }

    public void IncrementIterations()
    {
        lock (_lock) Iterations++;
    }

    public void SetIterations(int iterations)
    {
        lock (_lock) Iterations = iterations;
    }

    public void IncrementRetries()
    {
        lock (_lock) Retries++;
    }

    public void IncrementTools()
    {
        lock (_lock) ToolsExecuted++;
    }

    public void AddTokens(int prompt, int completion)
    {
        lock (_lock)
        {
            PromptTokens += prompt;
            CompletionTokens += completion;
        }
    }

    public void MarkCompleted(string finalResponse)
    {
        lock (_lock)
        {
            Status = RunStatus.Completed;
            FinalResponse = finalResponse;
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkFailed(string error, string? reason = null)
    {
        lock (_lock)
        {
            Status = RunStatus.Failed;
            Error = error;
            Reason = reason;
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkCancelled(string? reason = null)
    {
        lock (_lock)
        {
            Status = RunStatus.Cancelled;
            Reason = reason;
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkTimedOut()
    {
        lock (_lock)
        {
            Status = RunStatus.TimedOut;
            Reason = "Timeout exceeded";
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    internal void LoadStepFromStorage(RunStep step)
    {
        lock (_lock) _steps.Add(step);
    }

    internal void ForceFinishedAt(DateTimeOffset? value) { lock (_lock) FinishedAt = value; }
    internal void ForceStatus(RunStatus status) { lock (_lock) Status = status; }
    internal void ForceModel(string? model) { lock (_lock) Model = model; }
    internal void ForcePlan(Plan? plan) { lock (_lock) Plan = plan; }
    internal void ForceFinalResponse(string? response) { lock (_lock) FinalResponse = response; }
    internal void ForceError(string? error) { lock (_lock) Error = error; }
    internal void ForceReason(string? reason) { lock (_lock) Reason = reason; }
    internal void ForceIterations(int count) { lock (_lock) Iterations = count; }
    internal void ForceRetries(int count) { lock (_lock) Retries = count; }
    internal void ForceToolsExecuted(int count) { lock (_lock) ToolsExecuted = count; }
    internal void ForceTokens(long prompt, long completion) { lock (_lock) { PromptTokens = prompt; CompletionTokens = completion; } }
    internal void ForcePlugins(List<string> plugins) { lock (_lock) { _plugins.Clear(); _plugins.AddRange(plugins); } }
    internal void ForceReasoningTrace(List<string> trace) { lock (_lock) { _reasoningTrace.Clear(); _reasoningTrace.AddRange(trace); } }
    internal void ForceMemoryNotes(List<string> notes) { lock (_lock) { _memoryNotes.Clear(); _memoryNotes.AddRange(notes); } }
}

public interface IRunHistory
{
    RunRecord CreateRun(string name);
    RunRecord? Get(Guid runId);
    IReadOnlyList<RunRecord> GetAll();
    IReadOnlyList<RunRecord> GetRecent(int count = 50);
    int Count { get; }
    void Clear();
    Task InitializeAsync(CancellationToken ct = default);
    Task PersistAsync(RunRecord record, CancellationToken ct = default);
}

public sealed class InMemoryRunHistory : IRunHistory
{
    private readonly ConcurrentDictionary<Guid, RunRecord> _runs = new();

    public RunRecord CreateRun(string name)
    {
        var record = new RunRecord(Guid.NewGuid(), name, DateTimeOffset.UtcNow);
        _runs[record.RunId] = record;
        return record;
    }

    public RunRecord? Get(Guid runId)
        => _runs.TryGetValue(runId, out var record) ? record : null;

    public IReadOnlyList<RunRecord> GetAll()
        => _runs.Values.OrderByDescending(r => r.StartedAt).ToList();

    public IReadOnlyList<RunRecord> GetRecent(int count = 50)
        => GetAll().Take(Math.Max(0, count)).ToList();

    public int Count => _runs.Count;

    public void Clear() => _runs.Clear();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task PersistAsync(RunRecord record, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class DurableRunHistory : IRunHistory
{
    private readonly ConcurrentDictionary<Guid, RunRecord> _runs = new();
    private readonly IRunDataStore _store;
    private bool _loaded;

    public DurableRunHistory(IRunDataStore store)
    {
        _store = store;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _store.InitializeAsync(ct);
        var all = await _store.GetAllAsync(ct);
        foreach (var r in all)
            _runs[r.RunId] = r;
        _loaded = true;
    }

    public RunRecord CreateRun(string name)
    {
        var record = new RunRecord(Guid.NewGuid(), name, DateTimeOffset.UtcNow);
        _runs[record.RunId] = record;
        return record;
    }

    public RunRecord? Get(Guid runId)
        => _runs.TryGetValue(runId, out var record) ? record : null;

    public IReadOnlyList<RunRecord> GetAll()
        => _runs.Values.OrderByDescending(r => r.StartedAt).ToList();

    public IReadOnlyList<RunRecord> GetRecent(int count = 50)
        => GetAll().Take(Math.Max(0, count)).ToList();

    public int Count => _runs.Count;

    public void Clear()
    {
        _runs.Clear();
        _ = _store.ClearAsync();
    }

    public async Task PersistAsync(RunRecord record, CancellationToken ct = default)
    {
        if (_loaded)
            await _store.UpsertAsync(record, ct);
    }
}
