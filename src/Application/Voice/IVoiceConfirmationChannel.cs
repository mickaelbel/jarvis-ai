namespace JarvisAI.Application.Voice;

public sealed record VoiceConfirmationAnswer(bool Accepted, string? Raw);

/// <summary>
/// Canal permettant de poser une question à voix haute (TTS) puis de recueillir
/// la réponse parlée de l'utilisateur (STT). Utilisé pour les confirmations de
/// sécurité des outils dangereux dans le mode vocal Desktop.
/// </summary>
public interface IVoiceConfirmationChannel
{
    bool IsSupported { get; }
    Task<VoiceConfirmationAnswer?> AskAsync(string question, TimeSpan timeout, CancellationToken cancellationToken = default);
}
