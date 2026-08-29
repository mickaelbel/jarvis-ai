using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Moteur de génération d'images ADAPTATIF : il essaie d'abord le moteur local
/// Stable Diffusion (ComfyUI) pour la meilleure qualité quand la machine a un
/// GPU capable, puis bascule automatiquement sur Pollinations (cloud 100%
/// gratuit et illimité) si le local est indisponible, sans modèle, ou en échec.
/// De cette façon l'application s'adapte à n'importe quel ordinateur : GPU
/// Nvidia => qualité max ; CPU/Pas de serveur local => solution cloud fiable.
/// </summary>
public sealed class AdaptiveImageGenerationService : IImageGenerationService
{
    private readonly ComfyUIImageGenerationService _local;
    private readonly PollinationsImageGenerationService _cloud;
    private readonly ILogger<AdaptiveImageGenerationService> _logger;
    private string? _localUnavailable;
    private DateTime _localDownSince = DateTime.MinValue;
    private static readonly TimeSpan ReProbeInterval = TimeSpan.FromSeconds(60);

    public AdaptiveImageGenerationService(
        ComfyUIImageGenerationService local,
        PollinationsImageGenerationService cloud,
        ILogger<AdaptiveImageGenerationService> logger)
    {
        _local = local;
        _cloud = cloud;
        _logger = logger;
    }

    public async Task<GeneratedImage> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        // Re-probe le moteur local (s'il a été marqué indisponible) après un
        // cooldown : ainsi démarrer ComfyUI en cours de session est détecté.
        bool canProbeLocal = _localUnavailable is null || DateTime.UtcNow - _localDownSince > ReProbeInterval;

        if (canProbeLocal)
        {
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
