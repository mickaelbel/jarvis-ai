using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Application.Services;

/// <summary>
/// Vérification locale de licence signée RSA-2048, sans serveur.
/// Format : Base64( payload_json ) . hex( signature )
/// La clé publique RSA est embarquée dans l'application ; la clé privée
/// ne quitte JAMAIS l'appareil du vendeur (script New-LicenseKey.ps1).
/// </summary>
public sealed class LicenseService
{
    // ── Clé publique RSA embarquée (PKCS#1 DER, 256 octets) ─────────────
    // Générée une seule fois via New-LicenseKey.ps1 -GenerateKeyPair,
    // puis encodée ici. Ce bloc EST la sécurité : la clé privée n'est
    // dans aucun fichier déployé.
    private const string PublicKeyBase64 =
        "REPLACE_ME_WITH_REAL_PUBLIC_KEY_BASE64";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly string _licensePath;

    public LicenseService() : this(DefaultPath) { }

    public LicenseService(string path) => _licensePath = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "license.dat");

    /// <summary>Vérifie la licence. Retourne le statut détaillé.</summary>
    public LicenseStatus Verify()
    {
        try
        {
            if (!File.Exists(_licensePath)) return LicenseStatus.Absent;
            var raw = File.ReadAllText(_licensePath).Trim();
            var dot = raw.LastIndexOf('.');
            if (dot <= 0) return LicenseStatus.Invalid;

            var payloadB64 = raw[..dot];
            var sigHex = raw[(dot + 1)..];
            var payloadBytes = Convert.FromBase64String(payloadB64);
            var signature = Convert.FromHexString(sigHex);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);
            var payload = JsonSerializer.Deserialize<LicensePayload>(payloadJson, JsonOpts);
            if (payload is null) return LicenseStatus.Invalid;

            if (payload.Expiry is { Length: > 0 } exp &&
                DateTime.TryParse(exp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var expiryUtc) &&
                expiryUtc < DateTime.UtcNow)
                return LicenseStatus.Expired;

            var publicKey = GetEmbeddedPublicKey();
            if (publicKey is null) return LicenseStatus.Invalid;

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            var valid = rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return valid ? LicenseStatus.Valid : LicenseStatus.Invalid;
        }
        catch
        {
            return LicenseStatus.Invalid;
        }
    }

    public LicensePayload? GetPayload()
    {
        try
        {
            if (!File.Exists(_licensePath)) return null;
            var raw = File.ReadAllText(_licensePath).Trim();
            var dot = raw.LastIndexOf('.');
            if (dot <= 0) return null;
            var payloadBytes = Convert.FromBase64String(raw[..dot]);
            return JsonSerializer.Deserialize<LicensePayload>(Encoding.UTF8.GetString(payloadBytes), JsonOpts);
        }
        catch { return null; }
    }

    public void Save(string licenseKey) => File.WriteAllText(_licensePath, licenseKey.Trim());

    /// <summary>
    /// Période d'essai sans licence : 14 jours à partir de la première
    /// exécution (fichier de marquage persisté).
    /// </summary>
    public static bool IsInTrialPeriod()
    {
        var trialPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "trial-start.dat");
        try
        {
            if (!File.Exists(trialPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(trialPath)!);
                File.WriteAllText(trialPath, DateTime.UtcNow.ToString("o"));
                return true;
            }
            var start = DateTime.Parse(File.ReadAllText(trialPath)).ToUniversalTime();
            return (DateTime.UtcNow - start).TotalDays < 14;
        }
        catch { return true; }
    }

    private static byte[]? GetEmbeddedPublicKey()
    {
        try { return Convert.FromBase64String(PublicKeyBase64); }
        catch { return null; }
    }
}

public enum LicenseStatus { Valid, Absent, Expired, Invalid }

public sealed class LicensePayload
{
    public string? Owner { get; set; }
    public string? Expiry { get; set; }
    public string? Tier { get; set; }
    public string? Id { get; set; }
}
