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

    public List<SessionStep> Steps { get; } = new();
    public List<string> Decisions { get; } = new();
    public List<string> Errors { get; } = new();
    public ConcurrentDictionary<string, string> AppState { get; } = new();
    public string? CurrentPlan { get; set; }
    public int ConsecutiveFailures { get; set; }
    public bool Cancelled { get; set; }

    public void RecordStep(string action, string? result, bool success, string? toolName = null)
    {
        Steps.Add(new SessionStep
        {
            StepNumber = Steps.Count + 1,
            Action = action,
            Result = result,
            Success = success,
            ToolName = toolName,
            Timestamp = DateTime.UtcNow
        });
        LastActivityAt = DateTime.UtcNow;
        if (success) ConsecutiveFailures = 0;
        else ConsecutiveFailures++;
    }

    public void RecordDecision(string decision)
    {
        Decisions.Add($"[{DateTime.UtcNow:HH:mm:ss}] {decision}");
        LastActivityAt = DateTime.UtcNow;
    }

    public void RecordError(string error)
    {
        Errors.Add($"[{DateTime.UtcNow:HH:mm:ss}] {error}");
        LastActivityAt = DateTime.UtcNow;
        ConsecutiveFailures++;
    }

    public void UpdateAppState(string key, string value)
    {
        AppState[key] = value;
        LastActivityAt = DateTime.UtcNow;
    }

    public string BuildContextSummary(int maxSteps = 20)
    {
        var recentSteps = Steps.Skip(Math.Max(0, Steps.Count - maxSteps)).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SESSION {SessionId:N8}");
        sb.AppendLine($"Objective: {Objective}");
        sb.AppendLine($"Steps: {Steps.Count} (failures: {ConsecutiveFailures})");
        if (CurrentPlan is not null)
            sb.AppendLine($"Plan: {CurrentPlan}");
        if (recentSteps.Count > 0)
        {
            sb.AppendLine("Recent steps:");
            foreach (var step in recentSteps)
                sb.AppendLine($"  #{step.StepNumber} [{(step.Success ? "OK" : "FAIL")}] {step.Action} => {step.Result?[..Math.Min(100, step.Result?.Length ?? 0)]}");
        }
        if (Errors.Count > 0)
        {
            sb.AppendLine($"Last errors:");
            foreach (var err in Errors.TakeLast(3))
                sb.AppendLine($"  {err}");
        }
        if (AppState.Count > 0)
        {
            sb.AppendLine("App state:");
            foreach (var kv in AppState.TakeLast(5))
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
        }
        return sb.ToString();
    }

    public bool ShouldAbort(int maxSteps = 50, int maxConsecutiveFailures = 5)
    {
        if (Cancelled) return true;
        if (Steps.Count >= maxSteps) return true;
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
    private Guid? _activeSessionId;
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
        _activeSessionId = session.SessionId;
        _logger.LogInformation("[SessionManager] Created session {Id} for: {Objective}", session.SessionId.ToString("N8"), objective);
        return session;
    }

    public AgentSession? GetSession(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public AgentSession? GetActiveSession()
        => _activeSessionId.HasValue ? GetSession(_activeSessionId.Value) : null;

    public IReadOnlyList<AgentSession> GetRecentSessions(int count = 10)
        => _sessions.Values.OrderByDescending(s => s.StartedAt).Take(count).ToList();

    public void EndSession(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.RecordDecision("Session ended");
            if (_activeSessionId == sessionId)
                _activeSessionId = null;
            _logger.LogInformation("[SessionManager] Ended session {Id} ({Steps} steps)", sessionId.ToString("N8"), session.Steps.Count);
        }
    }
}
