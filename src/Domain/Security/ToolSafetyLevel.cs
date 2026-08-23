namespace JarvisAI.Domain.Security;

/// <summary>
/// Niveau de sûreté d'un outil, dans l'esprit N1/N2/N3 :
/// - N1 : sûr, exécution directe sans confirmation.
/// - N2 : sensible, confirmation demandée mais mémorisable (« toujours autoriser », révocable).
/// - N3 : critique, confirmation TOUJOURS exigée, jamais mémorisable, jamais contournable.
/// </summary>
public enum ToolSafetyLevel
{
    N1 = 1,
    N2 = 2,
    N3 = 3
}
