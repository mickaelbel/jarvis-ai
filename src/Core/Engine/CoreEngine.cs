using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Plugins;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Core.Engine;

public sealed class CoreEngine
{
    private readonly ILogger<CoreEngine> _logger;
    private readonly IEventBus _eventBus;
    private readonly IPluginManager _pluginManager;
    private EngineState _state = EngineState.Stopped;

    public EngineState State => _state;

    public CoreEngine(ILogger<CoreEngine> logger, IEventBus eventBus, IPluginManager pluginManager)
    {
        _logger = logger;
        _eventBus = eventBus;
        _pluginManager = pluginManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== Jarvis AI Starting ===");
        _state = EngineState.Starting;

        try
        {
            _logger.LogInformation("Discovering plugins...");
            await _pluginManager.DiscoverAsync(string.Empty, cancellationToken);

            _logger.LogInformation("Loading and starting plugins...");
            await _pluginManager.StartAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin initialization failed");
        }

        _state = EngineState.Running;
        _logger.LogInformation("=== Jarvis AI Ready ===");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== Jarvis AI Stopping ===");
        _state = EngineState.Stopping;

        try
        {
            await _pluginManager.StopAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping plugins");
        }

        _state = EngineState.Stopped;
        _logger.LogInformation("=== Jarvis AI Stopped ===");
    }
}

public enum EngineState
{
    Stopped,
    Starting,
    Running,
    Stopping
}
