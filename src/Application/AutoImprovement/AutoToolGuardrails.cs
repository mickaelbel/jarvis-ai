namespace JarvisAI.Application.AutoImprovement;

/// <summary>
/// Garde-fous : un outil auto-créé ne peut ni se casser lui-même (cœur immuable,
/// chemins protégés), ni masquer les outils de base (noms réservés).
/// </summary>
public static class AutoToolGuardrails
{
    /// <summary>Outils de base que l'IA ne doit jamais pouvoir masquer/remplacer.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system_info", "date_time", "memory", "file_system", "terminal", "process",
        "browser", "clipboard", "windows", "computer", "vision", "ui_element",
        "computer_use", "web_search", "create_tool", "add_lesson", "search",
        "chat", "math", "image", "audio", "calendar", "email", "plan", "agent"
    };

    /// <summary>Espace de travail accordé aux outils auto-créés (seule zone de lecture/écriture fichier).</summary>
    public static string WorkspaceRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI",
        "workspace");

    /// <summary>Répertoire où vivent les définitions persistées des outils auto-créés.</summary>
    public static string AutoRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI",
        "auto");

    public static bool IsNameValid(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length is >= 3 and <= 40
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
           && name[0] is >= 'a' and <= 'z';

    public static bool IsReserved(string name) => ReservedNames.Contains(name);

    public static bool IsRiskLevelValid(string risk)
        => string.Equals(risk, "low", StringComparison.OrdinalIgnoreCase)
           || string.Equals(risk, "medium", StringComparison.OrdinalIgnoreCase)
           || string.Equals(risk, "high", StringComparison.OrdinalIgnoreCase);

    /// <summary>Vrai si le chemin est dans le workspace alloué (et donc accessible en lecture/écriture).</summary>
    public static bool IsPathAllowedForFileAccess(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(WorkspaceRoot);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && full.Length > root.Length
                   && full[root.Length] == Path.DirectorySeparatorChar;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Vrai si le chemin est protégé (hors workspace) — l'accès doit être refusé.</summary>
    public static bool IsPathProtected(string path) => !IsPathAllowedForFileAccess(path);
}
