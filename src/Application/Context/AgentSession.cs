using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Application.Context;

/// <summary>
/// Tracks the current objective, plan, actions taken, results, errors, decisions,
/// and app state across steps of an autonomous agent run. This is the agent's
/// "working memory" — the central state object that persists across the
/// OBSERVE -> UNDERSTAND -> PLAN -> ACT -> VERIFY loop.
/// </summary>
public sealed class AgentSession
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public string Objective { get; set; } = string.Empty;
    public string? OriginalRequest { get; set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    private readonly object _sync = new();
    private readonly List<SessionStep> _steps = new();
    private readonly List<string> _decisions = new();
    private readonly List<string> _errors = new();

    public IReadOnlyList<SessionStep> Steps
    {
        get { lock (_sync) return _steps.ToList(); }
    }
    public IReadOnlyList<string> Decisions
    {
        get { lock (_sync) return _decisions.ToList(); }
    }
    public IReadOnlyList<string> Errors
    {
        get { lock (_sync) return _errors.ToList(); }
    }
    public ConcurrentDictionary<string, string> AppState { get; } = new();
    public string? CurrentPlan { get; set; }
    public int ConsecutiveFailures { get; set; }
    public volatile bool Cancelled;

    public int StepCount
    {
        get { lock (_sync) return _steps.Count; }
    }

    public void RecordStep(string action, string? result, bool success, string? toolName = null)
    {
        lock (_sync)
        {
            _steps.Add(new SessionStep
            {
                StepNumber = _steps.Count + 1,
                Action = action,
                Result = result,
                Success = success,
                ToolName = toolName,
                Timestamp = DateTime.UtcNow
            });
            if (success) ConsecutiveFailures = 0;
            else ConsecutiveFailures++;
        }
        LastActivityAt = DateTime.UtcNow;
    }

    public void RecordDecision(string decision)
    {
        lock (_sync)
        {
            _decisions.Add($"[{DateTime.UtcNow:HH:mm:ss}] {decision}");
        }
        LastActivityAt = DateTime.UtcNow;
    }

    public void RecordError(string error)
    {
        lock (_sync)
        {
            _errors.Add($"[{DateTime.UtcNow:HH:mm:ss}] {error}");
            ConsecutiveFailures++;
        }
        LastActivityAt = DateTime.UtcNow;
    }

    public void UpdateAppState(string key, string value)
    {
        AppState[key] = value;
        LastActivityAt = DateTime.UtcNow;
    }

    public string BuildContextSummary(int maxSteps = 20)
    {
        IReadOnlyList<SessionStep> recentSteps;
        IReadOnlyList<string> lastErrors;
        IReadOnlyList<KeyValuePair<string, string>> lastAppState;
        int stepCount;
        var consecutiveFailures = ConsecutiveFailures;

        lock (_sync)
        {
            recentSteps = _steps.Skip(Math.Max(0, _steps.Count - maxSteps)).ToList();
            lastErrors = _errors.TakeLast(3).ToList();
            lastAppState = AppState.TakeLast(5).ToList();
            stepCount = _steps.Count;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SESSION {SessionId:N8}");
        sb.AppendLine($"Objective: {Objective}");
        sb.AppendLine($"Steps: {stepCount} (failures: {consecutiveFailures})");
        if (CurrentPlan is not null)
            sb.AppendLine($"Plan: {CurrentPlan}");
        if (recentSteps.Count > 0)
        {
            sb.AppendLine("Recent steps:");
            foreach (var step in recentSteps)
            {
                var result = step.Result is { Length: > 0 }
                    ? step.Result[..Math.Min(100, step.Result.Length)]
                    : "(no result)";
                sb.AppendLine($"  #{step.StepNumber} [{(step.Success ? "OK" : "FAIL")}] {step.Action} => {result}");
            }
        }
        if (lastErrors.Count > 0)
        {
            sb.AppendLine($"Last errors:");
            foreach (var err in lastErrors)
                sb.AppendLine($"  {err}");
        }
        if (lastAppState.Count > 0)
        {
            sb.AppendLine("App state:");
            foreach (var kv in lastAppState)
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
        }
        return sb.ToString();
    }

    public bool ShouldAbort(int maxSteps = 50, int maxConsecutiveFailures = 5)
    {
        if (Cancelled) return true;
        if (StepCount >= maxSteps) return true;
        if (ConsecutiveFailures >= maxConsecutiveFailures) return true;
        return false;
    }
}

public sealed class SessionStep
{
    public int StepNumber { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Result { get; set; }
    public bool Success { get; set; }
    public string? ToolName { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Manages agent sessions: creation, lookup, and lifecycle.
/// </summary>
public interface ISessionManager
{
    AgentSession CreateSession(string objective, string? originalRequest = null);
    AgentSession? GetSession(Guid sessionId);
    AgentSession? GetActiveSession();
    IReadOnlyList<AgentSession> GetRecentSessions(int count = 10);
    void EndSession(Guid sessionId);
}

public sealed class SessionManager : ISessionManager
{
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();
    private string? _activeSessionId;
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(ILogger<SessionManager> logger)
    {
        _logger = logger;
    }

    public AgentSession CreateSession(string objective, string? originalRequest = null)
    {
        var session = new AgentSession
        {
            Objective = objective,
            OriginalRequest = originalRequest
        };
        _sessions[session.SessionId] = session;
        Interlocked.Exchange(ref _activeSessionId, session.SessionId.ToString("N"));
        _logger.LogInformation("[SessionManager] Created session {Id} for: {Objective}", session.SessionId.ToString("N8"), objective);
        return session;
    }

    public AgentSession? GetSession(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public AgentSession? GetActiveSession()
    {
        var id = Volatile.Read(ref _activeSessionId);
        return id is null ? null : GetSession(Guid.Parse(id));
    }

    public IReadOnlyList<AgentSession> GetRecentSessions(int count = 10)
        => _sessions.Values.OrderByDescending(s => s.StartedAt).Take(count).ToList();

    public void EndSession(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.RecordDecision("Session ended");
            if (Guid.TryParse(Volatile.Read(ref _activeSessionId), out var active) && active == sessionId)
                Volatile.Write(ref _activeSessionId, string.Empty);
            _logger.LogInformation("[SessionManager] Ended session {Id} ({Steps} steps)", sessionId.ToString("N8"), session.StepCount);
        }
    }
}
