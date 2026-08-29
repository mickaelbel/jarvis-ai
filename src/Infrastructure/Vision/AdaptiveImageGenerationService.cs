using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Moteur de génération d'images ADAPTATIF : il essaie d'abord le moteur local
/// Stable Diffusion (ComfyUI) pour la meilleure qualité quand la machine a un
/// GPU capable, puis bascule automatiquement sur Pollinations (cloud 100%
/// gratuit et illimité) si le local est indisponible, sans modèle, ou en échec.
/// Le moteur local est lancé automatiquement via ComfyUIProcessManager.
/// </summary>
public sealed class AdaptiveImageGenerationService : IImageGenerationService
{
    private readonly ComfyUIProcessManager _processManager;
    private readonly ComfyUIImageGenerationService _local;
    private readonly PollinationsImageGenerationService _cloud;
    private readonly ILogger<AdaptiveImageGenerationService> _logger;
    private string? _localUnavailable;
    private DateTime _localDownSince = DateTime.MinValue;
    private static readonly TimeSpan ReProbeInterval = TimeSpan.FromSeconds(60);

    public AdaptiveImageGenerationService(
        ComfyUIProcessManager processManager,
        ComfyUIImageGenerationService local,
        PollinationsImageGenerationService cloud,
        ILogger<AdaptiveImageGenerationService> logger)
    {
        _processManager = processManager;
        _local = local;
        _cloud = cloud;
        _logger = logger;
    }

    public async Task<GeneratedImage> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        bool canProbeLocal = _localUnavailable is null || DateTime.UtcNow - _localDownSince > ReProbeInterval;

        if (canProbeLocal)
        {
            // Auto-démarrer ComfyUI si installé mais pas encore lancé
            if (!_processManager.IsRunning && _processManager.IsInstalled)
            {
                _logger.LogInformation("[AdaptiveImage] ComfyUI installé mais pas lancé → démarrage automatique...");
                var started = await _processManager.EnsureRunningAsync(cancellationToken);
                if (!started)
                {
                    _localUnavailable = "ComfyUI impossible à démarrer";
                    _localDownSince = DateTime.UtcNow;
                    _logger.LogWarning("[AdaptiveImage] Échec démarrage ComfyUI → bascule cloud");
                    return await _cloud.GenerateImageAsync(prompt, cancellationToken);
                }
            }

            var local = await _local.GenerateImageAsync(prompt, cancellationToken);
            if (local.Success)
            {
                _localUnavailable = null;
                _logger.LogInformation("[AdaptiveImage] Moteur LOCAL (Stable Diffusion) utilisé");
                return local;
            }

            _localUnavailable = local.ErrorMessage ?? "moteur local indisponible";
            _localDownSince = DateTime.UtcNow;
            _logger.LogWarning("[AdaptiveImage] Bascule sur le moteur cloud (raison : {Reason})", _localUnavailable);
        }
        else
        {
            _logger.LogInformation("[AdaptiveImage] Moteur local marqué indisponible ({Reason}), passage direct au cloud", _localUnavailable);
        }

        return await _cloud.GenerateImageAsync(prompt, cancellationToken);
    }
}
