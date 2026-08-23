using JarvisAI.Application.Voice;

namespace JarvisAI.Web.Services;

/// <summary>
/// Sélecteur de modèle vocal intelligent : délègue au service de recommandation
/// (modèle rapide pour les demandes simples, modèle puissant sinon). Le bruit
/// ambiant n'est pas utilisé ici, seule la demande compte.
/// </summary>
public sealed class SmartVoiceModelPicker : IVoiceModelPicker
{
    private readonly ModelRecommendationService _recommendations;

    public SmartVoiceModelPicker(ModelRecommendationService recommendations)
    {
        _recommendations = recommendations;
    }

    public async Task<string?> PickModelAsync(string command, CancellationToken cancellationToken = default)
    {
        var route = await _recommendations.ResolveAsync(command, null, allowMultiStep: false, cancellationToken);
        return route.Model;
    }
}
