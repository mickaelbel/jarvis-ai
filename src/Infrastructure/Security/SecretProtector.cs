using System.Security.Cryptography;
using System.Text;
using System.Reflection;

namespace JarvisAI.Infrastructure.Security;

// Chiffrement des secrets au repos via DPAPI (CurrentUser) : les tokens ne
// sont plus jamais en clair dans %LOCALAPPDATA%. Transparent pour le reste du
// code — Protect/Unprotect sont appliqués en profondeur sur les objets settings.
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JarvisAI.secrets.v1");

    // Propriétés considérées comme sensibles, où qu'elles soient dans le graphe.
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClientSecret", "RefreshToken", "MotDePasseApp", "BotToken",
        "AuthToken", "AccessToken", "ApiKey", "Token"
    };

    public static void Protect(object? root) => Walk(root, protect: true);
    public static void Unprotect(object? root) => Walk(root, protect: false);

    public static string ProtectValue(string plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.StartsWith(Prefix, StringComparison.Ordinal))
            return plain;
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static string UnprotectValue(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return ""; // données corrompues ou d'une autre session Windows : pas de fuite
        }
    }

    private static void Walk(object? node, bool protect)
    {
        if (node is null) return;
        var type = node.GetType();
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum) return;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            var value = prop.GetValue(node);
            if (value is null) continue;

            if (prop.PropertyType == typeof(string))
            {
                if (SecretNames.Contains(prop.Name))
                {
                    var s = (string)value;
                    prop.SetValue(node, protect ? ProtectValue(s) : UnprotectValue(s));
                }
            }
            else if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is null || item.GetType() == typeof(string)) continue;
                    Walk(item, protect);
                }
            }
            else
            {
                Walk(value, protect);
            }
        }
    }
}

// Écritures atomiques avec rotation de backup : jamais de JSON à moitié écrit
// après une coupure de courant, et une version précédente récupérable.
public static class SafeFileWriter
{
    public static void WriteText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}