using JarvisAI.Application.Voice;

namespace JarvisAI.Application.Services;

/// <summary>
/// Baisse le volume des autres apps pendant la conversation vocale
/// (audio ducking). Le volume est restauré quand la conversation s'arrête.
/// </summary>
public interface IAudioDuckingService : IDisposable
{
    /// <summary>
    /// Baisse le volume de toutes les sessions audio selon les settings.
    /// Jarvis est toujours exclu. Les processus dans ExcludedApps aussi.
    /// </summary>
    Task DuckAsync(VoiceSettings settings);

    /// <summary>
    /// Restaure les volumes originaux avec un fondu enchaîné.
    /// </summary>
    Task RestoreAsync();

    /// <summary>True si le ducking est actuellement actif.</summary>
    bool IsDucking { get; }

    /// <summary>Toggle ducking on/off.</summary>
    Task ToggleAsync(VoiceSettings settings);
}
