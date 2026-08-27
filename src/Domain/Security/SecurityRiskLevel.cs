namespace JarvisAI.Domain.Security;

/// <summary>
/// Niveau de risque d'un outil/action, du plus sûr au plus critique.
/// Les valeurs numériques de Low/Medium/High sont conservées pour ne pas
/// casser les comparaisons et les données existantes ; Safe et Critical
/// sont ajoutés aux deux extrémités de l'échelle.
/// </summary>
public enum SecurityRiskLevel
{
    /// <summary>Zéro impact : lecture seule, informations triviales (heure, infos système).</summary>
    Safe = -1,

    /// <summary>Effet local et sans conséquence, exécuté sans confirmation.</summary>
    Low = 0,

    /// <summary>Effet sensible mais réversible / non destructif. Confirmation possible, mémorisable.</summary>
    Medium = 1,

    /// <summary>Effet significatif ou patrimonial. Confirmation exigée.</summary>
    High = 2,

    /// <summary>Effet irréversible, destructif ou sur du contenu critique. Confirmation TOUJOURS exigée, jamais mémorisable.</summary>
    Critical = 3
}
