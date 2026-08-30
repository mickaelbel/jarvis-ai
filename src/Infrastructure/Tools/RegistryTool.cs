using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JarvisAI.Infrastructure.Tools;

public sealed class RegistryTool : ITool
{
    private readonly ILogger<RegistryTool> _logger;

    public string Name => "registry";
    public string Description => "Lit et modifie le registre Windows. Actions: read_key (lit une valeur), write_key (écrit une valeur), list_subkeys (liste les sous-clés), delete_key (supprime une clé/valeur), search (recherche dans le registre). Utilise pour configurer des applications, modifier des paramètres système, etc.";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "read_key, write_key, list_subkeys, delete_key, search", typeof(string), required: true),
        new ToolParameter("key_path", "Chemin de la clé (ex: HKLM\\SOFTWARE\\Microsoft\\Windows)", typeof(string), required: true),
        new ToolParameter("value_name", "Nom de la valeur (pour read/write/delete)", typeof(string)),
        new ToolParameter("value_data", "Données à écrire (pour write_key)", typeof(string)),
        new ToolParameter("value_type", "Type: REG_SZ, REG_DWORD, REG_QWORD, REG_EXPAND_SZ (défaut: REG_SZ)", typeof(string)),
        new ToolParameter("search_term", "Terme à rechercher (pour search)", typeof(string)),
    };

    public RegistryTool(ILogger<RegistryTool> logger)
    {
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("key_path", out var keyPath);
        parameters.TryGetValue("value_name", out var valueName);
        parameters.TryGetValue("value_data", out var valueData);
        parameters.TryGetValue("value_type", out var valueType);
        parameters.TryGetValue("search_term", out var searchTerm);

        if (string.IsNullOrWhiteSpace(keyPath))
            return ToolResult.Failed("Paramètre 'key_path' requis (ex: HKLM\\SOFTWARE\\Microsoft)");

        return action?.ToLowerInvariant() switch
        {
            "read_key" => await ReadKeyAsync(keyPath, valueName, cancellationToken),
            "write_key" => await WriteKeyAsync(keyPath, valueName, valueData, valueType, cancellationToken),
            "list_subkeys" => await ListSubkeysAsync(keyPath, cancellationToken),
            "delete_key" => await DeleteKeyAsync(keyPath, valueName, cancellationToken),
            "search" => await SearchAsync(keyPath, searchTerm, cancellationToken),
            _ => ToolResult.Failed($"Action inconnue: {action}. Valides: read_key, write_key, list_subkeys, delete_key, search")
        };
    }

    private async Task<ToolResult> ReadKeyAsync(string keyPath, string? valueName, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var key = OpenKey(keyPath, false);
                if (key is null)
                    return ToolResult.Failed($"Clé non trouvée: {keyPath}");

                if (string.IsNullOrWhiteSpace(valueName))
                {
                    // Lire la valeur par défaut
                    var defaultVal = key.GetValue(null);
                    return ToolResult.Succeeded($"Valeur par défaut: {defaultVal ?? "(vide)"}");
                }

                var value = key.GetValue(valueName);
                var kind = key.GetValueKind(valueName);
                if (value is null)
                    return ToolResult.Failed($"Valeur '{valueName}' non trouvée dans {keyPath}");

                return ToolResult.Succeeded($"{valueName} ({kind}): {value}");
            }
            catch (Exception ex)
            {
                return ToolResult.Failed($"Erreur lecture registre: {ex.Message}");
            }
        }, ct);
    }

    private async Task<ToolResult> WriteKeyAsync(string keyPath, string? valueName, string? valueData, string? valueType, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var key = OpenKey(keyPath, true);
                if (key is null)
                    return ToolResult.Failed($"Impossible d'ouvrir/créer: {keyPath}");

                var kind = ParseValueKind(valueType);
                key.SetValue(valueName ?? "", valueData ?? "", kind);
                _logger.LogInformation("[RegistryTool] Wrote {ValueName} to {KeyPath}", valueName, keyPath);
                return ToolResult.Succeeded($"Écrit: {valueName ?? "(défaut)"} = {valueData} dans {keyPath}");
            }
            catch (Exception ex)
            {
                return ToolResult.Failed($"Erreur écriture registre: {ex.Message}");
            }
        }, ct);
    }

    private async Task<ToolResult> ListSubkeysAsync(string keyPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var key = OpenKey(keyPath, false);
                if (key is null)
                    return ToolResult.Failed($"Clé non trouvée: {keyPath}");

                var subKeys = key.GetSubKeyNames();
                var values = key.GetValueNames();

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Clé: {keyPath}");

                if (subKeys.Length > 0)
                {
                    sb.AppendLine($"Sous-clés ({subKeys.Length}):");
                    foreach (var sk in subKeys.Take(50))
                        sb.AppendLine($"  [{sk}]");
                    if (subKeys.Length > 50)
                        sb.AppendLine($"  ... et {subKeys.Length - 50} autres");
                }

                if (values.Length > 0)
                {
                    sb.AppendLine($"Valeurs ({values.Length}):");
                    foreach (var v in values.Take(50))
                    {
                        var val = key.GetValue(v);
                        sb.AppendLine($"  {v ?? "(défaut)"} = {val}");
                    }
                }

                return ToolResult.Succeeded(sb.ToString());
            }
            catch (Exception ex)
            {
                return ToolResult.Failed($"Erreur liste registre: {ex.Message}");
            }
        }, ct);
    }

    private async Task<ToolResult> DeleteKeyAsync(string keyPath, string? valueName, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(valueName))
                {
                    // Supprimer une valeur
                    using var key = OpenKey(keyPath, true);
                    if (key is null)
                        return ToolResult.Failed($"Clé non trouvée: {keyPath}");

                    key.DeleteValue(valueName, false);
                    return ToolResult.Succeeded($"Valeur '{valueName}' supprimée de {keyPath}");
                }
                else
                {
                    // Supprimer une clé (récursif = false pour sécurité)
                    var parentPath = Path.GetDirectoryName(keyPath)?.Replace('/', '\\');
                    var keyName = Path.GetFileName(keyPath);
                    if (string.IsNullOrEmpty(parentPath))
                        return ToolResult.Failed("Impossible de déterminer le chemin parent");

                    using var parentKey = OpenKey(parentPath, true);
                    if (parentKey is null)
                        return ToolResult.Failed($"Clé parent non trouvée: {parentPath}");

                    parentKey.DeleteSubKeyTree(keyName, false);
                    return ToolResult.Succeeded($"Clé '{keyPath}' supprimée");
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Failed($"Erreur suppression registre: {ex.Message}");
            }
        }, ct);
    }

    private async Task<ToolResult> SearchAsync(string keyPath, string? searchTerm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return ToolResult.Failed("Paramètre 'search_term' requis pour la recherche");

        return await Task.Run(() =>
        {
            try
            {
                using var key = OpenKey(keyPath, false);
                if (key is null)
                    return ToolResult.Failed($"Clé non trouvée: {keyPath}");

                var results = new List<string>();
                SearchRecursive(key, keyPath, searchTerm.ToLowerInvariant(), results, 0);

                if (results.Count == 0)
                    return ToolResult.Succeeded($"Aucun résultat pour '{searchTerm}' dans {keyPath}");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Résultats pour '{searchTerm}' ({results.Count} trouvés) :");
                foreach (var r in results.Take(30))
                    sb.AppendLine($"  {r}");
                if (results.Count > 30)
                    sb.AppendLine($"  ... et {results.Count - 30} autres");

                return ToolResult.Succeeded(sb.ToString());
            }
            catch (Exception ex)
            {
                return ToolResult.Failed($"Erreur recherche registre: {ex.Message}");
            }
        }, ct);
    }

    private void SearchRecursive(RegistryKey key, string basePath, string searchTerm, List<string> results, int depth)
    {
        if (depth > 5 || results.Count > 100) return;

        try
        {
            // Chercher dans les valeurs
            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName);
                if (value?.ToString()?.ToLowerInvariant().Contains(searchTerm) == true ||
                    valueName.ToLowerInvariant().Contains(searchTerm))
                {
                    results.Add($"{basePath}\\{valueName} = {value}");
                }
            }

            // Chercher dans les sous-clés
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey is not null)
                        SearchRecursive(subKey, $"{basePath}\\{subKeyName}", searchTerm, results, depth + 1);
                }
                catch { }
            }
        }
        catch { }
    }

    private static RegistryKey? OpenKey(string keyPath, bool writable)
    {
        // Parser le root key
        var parts = keyPath.Split('\\', 2);
        if (parts.Length < 2) return null;

        var rootName = parts[0].ToUpperInvariant();
        var subPath = parts.Length > 1 ? parts[1] : "";

        var root = rootName switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Registry.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => Registry.CurrentConfig,
            _ => null
        };

        if (root is null) return null;
        return writable ? root.CreateSubKey(subPath) : root.OpenSubKey(subPath);
    }

    private static RegistryValueKind ParseValueKind(string? kind) => kind?.ToUpperInvariant() switch
    {
        "REG_DWORD" => RegistryValueKind.DWord,
        "REG_QWORD" => RegistryValueKind.QWord,
        "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
        "REG_MULTI_SZ" => RegistryValueKind.MultiString,
        "REG_BINARY" => RegistryValueKind.Binary,
        _ => RegistryValueKind.String
    };
}
