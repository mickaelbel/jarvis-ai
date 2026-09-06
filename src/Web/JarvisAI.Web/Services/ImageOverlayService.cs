using JarvisAI.Application.Abstractions;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Souscrit aux événements ImageGeneratedEvent et affiche l'image/vidéo en overlay sur le bureau.
/// </summary>
public sealed class ImageOverlayService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly ILogger<ImageOverlayService> _logger;

    public ImageOverlayService(IEventBus eventBus, IHubContext<OverlayHub> hub, ILogger<ImageOverlayService> logger)
    {
        _eventBus = eventBus;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<ImageGeneratedEvent>(OnImageGenerated);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private Task OnImageGenerated(ImageGeneratedEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(evt.FilePath)) return Task.CompletedTask;

        try
        {
            if (evt.MediaType == "video")
            {
                _logger.LogInformation("[Overlay] Showing video: {Path}", evt.FilePath);
                return _hub.Clients.Group("desktop").SendAsync("overlayVideo", evt.FilePath, null, null, null, ct);
            }
            else
            {
                _logger.LogInformation("[Overlay] Showing image: {Path}", evt.FilePath);
                return _hub.Clients.Group("desktop").SendAsync("overlayImage", evt.FilePath, null, null, null, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Overlay] Failed to show overlay");
            return Task.CompletedTask;
        }
    }
}
