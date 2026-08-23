namespace JarvisAI.Application.Voice;

/// <summary>
/// Choisit le modèle à utiliser pour une commande vocale (sélection intelligente :
/// modèle rapide pour les demandes simples, modèle puissant sinon).
/// </summary>
public interface IVoiceModelPicker
{
    Task<string?> PickModelAsync(string command, CancellationToken cancellationToken = default);
}
