namespace JarvisAI.Web.Services;

/// <summary>
/// Manages the lifetime of AI generation runs in the chat UI. It owns the
/// CancellationTokenSource used to stop streaming, tool calls and UI updates.
/// Multiple generations may overlap (a backgrounded task finishing after a newer
/// one has started): <see cref="Complete"/> is scoped to the token of the run
/// that finishes, so a background task never disposes a newer run's token source.
/// </summary>
public sealed class GenerationSession : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    /// <summary>True while a generation is in progress and not cancelled.</summary>
    public bool IsActive
    {
        get { lock (_gate) return _cts is not null && !_cts.IsCancellationRequested; }
    }

    /// <summary>True once the current generation has been cancelled.</summary>
    public bool IsCancellationRequested
    {
        get { lock (_gate) return _cts?.IsCancellationRequested ?? false; }
    }

    /// <summary>The cancellation token used by the running generation.</summary>
    public CancellationToken Token
    {
        get { lock (_gate) return _cts?.Token ?? CancellationToken.None; }
    }

    /// <summary>Starts a new generation, discarding any previous session.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }
    }

    /// <summary>Requests cancellation of the current generation. Safe to call twice.</summary>
    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }

    /// <summary>
    /// Ends the generation that owns <paramref name="completedToken"/> and releases
    /// its token source. When called with the default token it ends the current
    /// generation. A finishing background task passes its own token so a newer,
    /// still-running generation is left untouched.
    /// </summary>
    public void Complete(CancellationToken completedToken = default)
    {
        lock (_gate)
        {
            var cts = _cts;
            if (cts is null) return;
            if (completedToken == default || cts.Token == completedToken)
            {
                cts.Dispose();
                _cts = null;
            }
        }
    }

    /// <summary>Cancels any running generation and releases the session.</summary>
    public void Dispose()
    {
        CancellationTokenSource? toRelease;
        lock (_gate)
        {
            toRelease = _cts;
            _cts = null;
        }
        toRelease?.Cancel();
        toRelease?.Dispose();
    }
}
