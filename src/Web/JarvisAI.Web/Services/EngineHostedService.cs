using JarvisAI.Core.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public sealed class EngineHostedService : IHostedService
{
    private readonly CoreEngine _engine;
    private readonly ILogger<EngineHostedService> _logger;

    public EngineHostedService(CoreEngine engine, ILogger<EngineHostedService> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _engine.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EngineHostedService] Failed to start engine");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _engine.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EngineHostedService] Failed to stop engine");
        }
    }
}
