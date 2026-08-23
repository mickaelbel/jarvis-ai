namespace JarvisAI.Web.Services;

/// <summary>A single toast notification shown by <c>ToastNotification.razor</c>.</summary>
public sealed class ToastMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Type { get; init; } = "info"; // success | error | info
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);
    public bool Expired(DateTimeOffset now) => now - CreatedAt >= Duration;
}

/// <summary>
/// Global, in-memory notification service used by pages and components to show
/// transient toasts. Registered per-circuit so each user sees their own stack.
/// </summary>
public sealed class ToastService
{
    private readonly List<ToastMessage> _toasts = new();
    private readonly object _lock = new();
    private readonly int _maxToasts;

    public event Action? Changed;

    public ToastService(int maxToasts = 5)
    {
        _maxToasts = maxToasts;
    }

    public IReadOnlyList<ToastMessage> Toasts
    {
        get { lock (_lock) return _toasts.ToList().AsReadOnly(); }
    }

    public void ShowSuccess(string message, string title = "Success")
        => Show(message, "success", title);

    public void ShowError(string message, string title = "Error")
        => Show(message, "error", title);

    public void ShowInfo(string message, string title = "Info")
        => Show(message, "info", title);

    public void Show(string message, string type = "info", string title = "")
    {
        lock (_lock)
        {
            _toasts.Add(new ToastMessage { Message = message, Type = type, Title = title });
            while (_toasts.Count > _maxToasts)
                _toasts.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_lock) _toasts.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    public void ClearExpired(DateTimeOffset now)
    {
        var removed = false;
        lock (_lock) removed = _toasts.RemoveAll(t => t.Expired(now)) > 0;
        if (removed) Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) _toasts.Clear();
        Changed?.Invoke();
    }
}
