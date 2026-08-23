using JarvisAI.Application.Abstractions;
using JarvisAI.Domain.Events;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Infrastructure.Events;

public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
    private readonly ILogger<InMemoryEventBus> _logger;

    public InMemoryEventBus(ILogger<InMemoryEventBus> logger)
    {
        _logger = logger;
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IEvent
    {
        _logger.LogDebug("Publishing event {EventType} (Id={EventId}, CorrelationId={CorrelationId})",
            @event.EventType, @event.EventId, @event.CorrelationId);

        var eventType = typeof(TEvent);
        var handlerTypes = _handlers.Keys.Where(t => t.IsAssignableFrom(eventType)).ToList();

        if (handlerTypes.Count == 0)
        {
            _logger.LogDebug("No handlers for event type {EventType}", @event.EventType);
            return;
        }

        foreach (var handlerType in handlerTypes)
        {
            if (!_handlers.TryGetValue(handlerType, out var handlers)) continue;
            foreach (var handler in handlers)
            {
                var typed = (Func<TEvent, CancellationToken, Task>)handler;
                await typed(@event, cancellationToken);
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IEvent
    {
        var type = typeof(TEvent);
        var list = _handlers.GetOrAdd(type, _ => new List<Delegate>());
        lock (list)
        {
            list.Add(handler);
        }

        _logger.LogDebug("Subscribed to event type {EventType}", type.Name);

        return new Unsubscriber(list, handler);
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly List<Delegate> _list;
        private readonly Delegate _handler;

        public Unsubscriber(List<Delegate> list, Delegate handler)
        {
            _list = list;
            _handler = handler;
        }

        public void Dispose()
        {
            lock (_list)
            {
                _list.Remove(_handler);
            }
        }
    }
}
