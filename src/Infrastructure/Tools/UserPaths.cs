using System;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Résout les chemins utilisateur : variables d'environnement, dossiers
/// publics remappés vers l'utilisateur courant, et noms conviviaux (français
/// ou anglais) de dossiers spéciaux (Bureau, Documents, Téléchargements...).
/// Utilisé par file_system, read_document et windows pour que l'assistant n'ait
/// jamais besoin d'inventer un chemin absolu.
/// </summary>
internal static class UserPaths
{
    public static string? GetSpecialFolderPath(string? name)
    {
        var lower = name?.Trim().ToLowerInvariant() ?? "";
        return lower switch
        {
            "desktop" or "bureau" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "documents" or "document" or "doc" or "mes documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "downloads" or "download" or "téléchargements" or "telechargements" or "téléchargement" or "telechargement" or "dl"
                => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "appdata" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "localappdata" or "local appdata" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "temp" or "tmp" => Path.GetTempPath(),
            "programfiles" or "program files" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "system" => Environment.SystemDirectory,
            _ => null
        };
    }

    public static string? ResolveUserPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        path = Environment.ExpandEnvironmentVariables(path);
        path = path.Replace('/', Path.DirectorySeparatorChar);

        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var userDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var publicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);

        // Remap public paths to user paths.
        if (!string.IsNullOrEmpty(publicDesktop) && path.StartsWith(publicDesktop.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(userDesktop, path[publicDesktop.Length..].TrimStart(Path.DirectorySeparatorChar));

        if (!string.IsNullOrEmpty(publicDocuments) && path.StartsWith(publicDocuments.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(userDocuments, path[publicDocuments.Length..].TrimStart(Path.DirectorySeparatorChar));

        // Friendly names ("Bureau", "Desktop", "Documents", "Téléchargements"...) as whole path or prefix.
        var trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar);
        var candidates = new (string Alias, string Target)[]
        {
            ("desktop", userDesktop), ("bureau", userDesktop),
            ("documents", userDocuments), ("document", userDocuments), ("doc", userDocuments), ("mes documents", userDocuments),
            ("downloads", downloads), ("download", downloads),
            ("téléchargements", downloads), ("telechargements", downloads),
            ("téléchargement", downloads), ("telechargement", downloads)
        };

        foreach (var (alias, target) in candidates)
        {
            if (trimmed.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return target;

            if (trimmed.StartsWith(alias + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(target, trimmed[(alias.Length + 1)..]);
        }

        return path;
    }
}
