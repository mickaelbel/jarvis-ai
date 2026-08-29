using JarvisAI.Application.Memory;
using Microsoft.Extensions.Hosting;

namespace JarvisAI.Infrastructure.Memory;

/// <summary>
/// Wrapper IHostedService pour démarrer/arrêter le MemoryConsolidationService.
/// </summary>
internal sealed class MemoryConsolidationHostedService : IHostedService
{
    private readonly MemoryConsolidationService _inner;

    public MemoryConsolidationHostedService(MemoryConsolidationService inner)
    {
        _inner = inner;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _inner.Start(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _inner.Stop();
        return Task.CompletedTask;
    }
}
