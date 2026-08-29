namespace JarvisAI.Application.Errors;

/// <summary>
/// Catégories d'erreurs pour le routing et les suggestions de récupération.
/// </summary>
public enum ErrorCategory
{
    Transient,   // Erreur temporaire (timeout, réseau) → retry
    Config,      // Configuration manquante/incorrecte → settings
    Auth,        // Authentification échouée → reconnexion
    Network,     // Pas de réseau / DNS → vérifier connexion
    NotFound,    // Ressource introuvable → modèle non installé
    Permission,  // Accès refusé → permissions
    Unknown      // Erreur inclassée
}

/// <summary>
/// Erreur structurée avec catégorie, message utilisateur et suggestion de récupération.
/// Remplace les messages vagues type "AI provider error" par des actions concrètes.
/// </summary>
public sealed record AppError
{
    public ErrorCategory Category { get; init; }
    public string UserMessage { get; init; } = "";
    public string RecoverySuggestion { get; init; } = "";
    public string? TechnicalDetails { get; init; }
    public string? RecoveryAction { get; init; } // ex: "open_settings", "download_model"

    public static AppError OllamaNotFound(string model) => new()
    {
        Category = ErrorCategory.NotFound,
        UserMessage = $"Modèle '{model}' non installé.",
        RecoverySuggestion = $"Exécuter : ollama pull {model}",
        RecoveryAction = "download_model",
        TechnicalDetails = $"Model '{model}' not found in Ollama registry"
    };

    public static AppError OllamaOffline() => new()
    {
        Category = ErrorCategory.Network,
        UserMessage = "Ollama n'est pas accessible.",
        RecoverySuggestion = "Vérifiez qu'Ollama est démarré (ollama serve) sur le port 11434.",
        RecoveryAction = "start_ollama"
    };

    public static AppError ApiKeyMissing(string provider) => new()
    {
        Category = ErrorCategory.Auth,
        UserMessage = $"Clé API manquante pour {provider}.",
        RecoverySuggestion = $"Ajoutez votre clé API dans Settings → Providers → {provider}.",
        RecoveryAction = "open_settings"
    };

    public static AppError RateLimited(string provider) => new()
    {
        Category = ErrorCategory.Transient,
        UserMessage = $"Limite de débit atteinte sur {provider}.",
        RecoverySuggestion = "Réessayez dans quelques secondes ou utilisez un autre provider.",
        RecoveryAction = "switch_provider"
    };

    public static AppError Timeout(string operation) => new()
    {
        Category = ErrorCategory.Transient,
        UserMessage = $"L'opération '{operation}' a pris trop de temps.",
        RecoverySuggestion = "Réessayez. Si le problème persiste, le modèle est peut-être trop lourd pour votre GPU.",
        TechnicalDetails = $"Operation '{operation}' timed out"
    };

    public static AppError ToolFailed(string toolName, string reason) => new()
    {
        Category = ErrorCategory.Unknown,
        UserMessage = $"L'outil '{toolName}' a échoué : {reason}",
        RecoverySuggestion = "Réessayez ou utilisez un autre outil.",
        TechnicalDetails = reason
    };

    public static AppError FromException(Exception ex, string context = "") => new()
    {
        Category = ex switch
        {
            System.Net.Http.HttpRequestException => ErrorCategory.Network,
            System.TimeoutException => ErrorCategory.Transient,
            System.UnauthorizedAccessException => ErrorCategory.Auth,
            System.IO.FileNotFoundException => ErrorCategory.NotFound,
            _ => ErrorCategory.Unknown
        },
        UserMessage = $"Erreur{(string.IsNullOrEmpty(context) ? "" : $" ({context})")}: {ex.Message}",
        RecoverySuggestion = "Vérifiez les logs pour plus de détails.",
        TechnicalDetails = ex.ToString()
    };
}
