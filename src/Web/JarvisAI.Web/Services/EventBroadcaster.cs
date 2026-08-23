using JarvisAI.Application.Abstractions;
using JarvisAI.Domain.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public sealed class EventBroadcaster : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IHubContext<Hubs.JarvisHub> _hubContext;
    private readonly ILogger<EventBroadcaster> _logger;
    private readonly ActionHistoryService _history;
    private readonly List<IDisposable> _subscriptions = new();

    public EventBroadcaster(IEventBus eventBus, IHubContext<Hubs.JarvisHub> hubContext, ILogger<EventBroadcaster> logger, ActionHistoryService history)
    {
        _eventBus = eventBus;
        _hubContext = hubContext;
        _logger = logger;
        _history = history;
    }

    public void Start()
    {
        SubscribeToAll<IEvent>(nameof(IEvent));
        _logger.LogInformation("[EventBroadcaster] Started broadcasting events");
    }

    private void SubscribeToAll<TEvent>(string eventType) where TEvent : IEvent
    {
        var sub = _eventBus.Subscribe<TEvent>(async (evt, ct) =>
        {
            try
            {
                var dto = new EventDto
                {
                    EventId = evt.EventId,
                    EventType = evt.EventType,
                    OccurredOn = evt.OccurredOn,
                    CorrelationId = evt.CorrelationId,
                    Payload = evt.ToString() ?? ""
                };
                await _hubContext.Clients.All.SendAsync("ReceiveEvent", dto, ct);

                _history.AddEntry(new HistoryEntry
                {
                    Timestamp = evt.OccurredOn,
                    Type = MapEventType(evt.EventType),
                    Title = evt.EventType,
                    Description = evt.ToString() ?? "",
                    Status = evt.EventType.Contains("Failed") || evt.EventType.Contains("Error") ? "error" : "info",
                    Details = new() { ["correlationId"] = evt.CorrelationId.ToString() }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EventBroadcaster] Failed to broadcast event {EventType}", evt.EventType);
            }
        });
        _subscriptions.Add(sub);
    }

    private static string MapEventType(string eventType) => eventType switch
    {
        string t when t.Contains("Memory") => "memory",
        string t when t.Contains("Tool") => "tool",
        string t when t.Contains("Command") => "command",
        string t when t.Contains("Agent") => "chat",
        string t when t.Contains("Plan") => "command",
        string t when t.Contains("Security") => "security",
        _ => "system"
    };

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
    }
}

public sealed class EventDto
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime OccurredOn { get; set; }
    public Guid CorrelationId { get; set; }
    public string Payload { get; set; } = string.Empty;
}
