using JarvisAI.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Core.Engine;

public sealed class CoreEngine
{
    private readonly ILogger<CoreEngine> _logger;
    private readonly IEventBus _eventBus;
    private EngineState _state = EngineState.Stopped;

    public EngineState State => _state;

    public CoreEngine(ILogger<CoreEngine> logger, IEventBus eventBus)
    {
        _logger = logger;
        _eventBus = eventBus;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== Jarvis AI Starting ===");
        _state = EngineState.Starting;

        _state = EngineState.Running;
        _logger.LogInformation("=== Jarvis AI Ready ===");
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== Jarvis AI Stopping ===");
        _state = EngineState.Stopping;

        _state = EngineState.Stopped;
        _logger.LogInformation("=== Jarvis AI Stopped ===");
        await Task.CompletedTask;
    }
}

public enum EngineState
{
    Stopped,
    Starting,
    Running,
    Stopping
}
