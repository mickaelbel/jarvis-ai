using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface INotificationService
{
    void Notify(string title, string message, NotificationType type = NotificationType.Info);
    void NotifyBatch(IReadOnlyList<NotificationItem> notifications);
    IReadOnlyList<NotificationItem> GetPending();
    void Dismiss(string notificationId);
    void ClearAll();
    void SetDoNotDisturb(bool enabled, TimeSpan? duration = null);
    bool IsDoNotDisturbActive { get; }
    event EventHandler<NotificationItem>? NotificationReceived;
}

public sealed class NotificationService : INotificationService
{
    private readonly ILogger<NotificationService> _logger;
    private readonly List<NotificationItem> _pending = new();
    private readonly List<NotificationItem> _history = new();
    private bool _dndActive;
    private Timer? _dndTimer;
    private const int MaxHistory = 200;

    public bool IsDoNotDisturbActive => _dndActive;

    public event EventHandler<NotificationItem>? NotificationReceived;

    public NotificationService(ILogger<NotificationService> logger)
    {
        _logger = logger;
    }

    public void Notify(string title, string message, NotificationType type = NotificationType.Info)
    {
        if (_dndActive && type != NotificationType.Critical)
        {
            _logger.LogDebug("[Notify] Suppressed (DnD): {Title}", title);
            return;
        }

        var notification = new NotificationItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Message = message,
            Type = type,
            Timestamp = DateTime.UtcNow
        };

        lock (_pending)
        {
            _pending.Add(notification);
        }

        lock (_history)
        {
            _history.Add(notification);
            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }

        NotificationReceived?.Invoke(this, notification);
        _logger.LogInformation("[Notify] {Type}: {Title} - {Message}", type, title, message);
    }

    public void NotifyBatch(IReadOnlyList<NotificationItem> notifications)
    {
        foreach (var n in notifications)
            Notify(n.Title, n.Message, n.Type);
    }

    public IReadOnlyList<NotificationItem> GetPending()
    {
        lock (_pending)
        {
            return _pending.ToList();
        }
    }

    public void Dismiss(string notificationId)
    {
        lock (_pending)
        {
            _pending.RemoveAll(n => n.Id == notificationId);
        }
    }

    public void ClearAll()
    {
        lock (_pending)
        {
            _pending.Clear();
        }
    }

    public void SetDoNotDisturb(bool enabled, TimeSpan? duration = null)
    {
        _dndActive = enabled;
        _dndTimer?.Dispose();

        if (enabled && duration.HasValue)
        {
            _dndTimer = new Timer(_ =>
            {
                _dndActive = false;
                _logger.LogInformation("[Notify] DnD automatically disabled");
            }, null, duration.Value, Timeout.InfiniteTimeSpan);
        }

        _logger.LogInformation("[Notify] Do Not Disturb: {Enabled}", enabled);
    }
}

public sealed class NotificationItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public NotificationType Type { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsRead { get; set; }
}

public enum NotificationType { Info, Success, Warning, Error, Critical }
