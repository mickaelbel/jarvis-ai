using System.Collections.Concurrent;

namespace JarvisAI.Infrastructure.AI;

public sealed record LlmRun(
    string? Model,
    int PromptTokens,
    int CompletionTokens,
    long EvalDurationMs,
    DateTimeOffset OccurredAt)
{
    public double TokensPerSecond
        => EvalDurationMs > 0 ? (double)CompletionTokens / EvalDurationMs * 1000 : 0;
}

public sealed class OllamaRunMonitor
{
    private const int MaxRecent = 100;
    private readonly ConcurrentQueue<LlmRun> _recent = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsedByModel = new();

    public void Record(string? model, int promptTokens, int completionTokens, long evalDurationMs, DateTimeOffset? occurredAt = null)
    {
        var run = new LlmRun(model, promptTokens, completionTokens, evalDurationMs, occurredAt ?? DateTimeOffset.UtcNow);

        _recent.Enqueue(run);
        while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }

        if (!string.IsNullOrWhiteSpace(model))
            _lastUsedByModel[model] = run.OccurredAt;
    }

    public IReadOnlyList<LlmRun> Recent => _recent.ToArray();

    public LlmRun? Last => _recent.TryPeek(out var last) ? last : null;

    public DateTimeOffset? GetLastActivity(string model)
        => _lastUsedByModel.TryGetValue(model, out var t) ? t : null;
}
