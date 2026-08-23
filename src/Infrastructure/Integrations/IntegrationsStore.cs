using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Integrations;

// Magasin centralisé des identifiants/intégrations (pattern SmartRoutingStore)
public sealed class IntegrationsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "integrations.json");

    private readonly object _lock = new();
    private IntegrationsSettings _settings;
    private readonly ILogger<IntegrationsStore> _logger;

    public IntegrationsStore(ILogger<IntegrationsStore> logger)
    {
        _logger = logger;
        _settings = Load();
    }

    public IntegrationsSettings Get()
    {
        lock (_lock) return Clone(_settings);
    }

    public void Save(IntegrationsSettings settings)
    {
        lock (_lock)
        {
            _settings = Clone(settings);
            Persist(_settings);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static IntegrationsSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new IntegrationsSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<IntegrationsSettings>(json, JsonOpts) ?? new IntegrationsSettings();
            JarvisAI.Infrastructure.Security.SecretProtector.Unprotect(settings);
            return settings;
        }
        catch (Exception ex)
        {
            // Logging not available in static Load, will be caught by caller
            return new IntegrationsSettings();
        }
    }

    private static void Persist(IntegrationsSettings settings)
    {
        // Copie chiffrée pour le disque ; l'objet en mémoire reste en clair.
        var snapshot = Clone(settings);
        JarvisAI.Infrastructure.Security.SecretProtector.Protect(snapshot);
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(SettingsPath, json);
    }

    private static IntegrationsSettings Clone(IntegrationsSettings s) =>
        JsonSerializer.Deserialize<IntegrationsSettings>(
            JsonSerializer.Serialize(s, JsonOpts), JsonOpts) ?? new IntegrationsSettings();
}

public sealed class IntegrationsSettings
{
    public GoogleSettings Google { get; set; } = new();
    public GmailSettings Gmail { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();
    public TwilioSettings Twilio { get; set; } = new();
    public InstagramSettings Instagram { get; set; } = new();
    public NestSettings Nest { get; set; } = new();
    public AlexaSettings Alexa { get; set; } = new();
    public HermesSettings Hermes { get; set; } = new();
    public MusiqueSettings Musique { get; set; } = new();
    public GestesSettings Gestes { get; set; } = new();
    public PresenceScenesSettings PresenceScenes { get; set; } = new();
    public HomeAssistantSettings HomeAssistant { get; set; } = new();
}

public sealed class HomeAssistantSettings
{
    /// <summary>URL du serveur HA, ex : http://homeassistant.local:8123</summary>
    public string Url { get; set; } = "";
    /// <summary>Long-lived access token (Profil → Sécurité → Jetons d'accès de longue durée)</summary>
    public string Token { get; set; } = "";
}

public sealed class GoogleSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string CalendrierPrincipal { get; set; } = "primary";
    public List<string> IcalUrls { get; set; } = new();
}

public sealed class GmailSettings
{
    public string Adresse { get; set; } = "";
    public string MotDePasseApp { get; set; } = ""; // IMAP fallback; OAuth via GoogleSettings
}

public sealed class DiscordSettings
{
    public string BotToken { get; set; } = "";
    public string UserId { get; set; } = "";
    public string GuildId { get; set; } = "";
}

public sealed class TwilioSettings
{
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string Numero { get; set; } = "";
    public List<string> PrefixesInterdits { get; set; } = new() { "089", "0899", "3", "118", "10" };
}

public sealed class InstagramSettings
{
    public List<InstagramAccount> Comptes { get; set; } = new();
}

public sealed class InstagramAccount
{
    public string Nom { get; set; } = "";
    public string UserId { get; set; } = "";
    public string AccessToken { get; set; } = "";
}

public sealed class NestSettings
{
    public string ProjectId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

public sealed class AlexaSettings
{
    public string Url { get; set; } = "amazon.fr";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string MacDms { get; set; } = "";
    public string SerialNumber { get; set; } = "";
}

public sealed class HermesSettings
{
    public string ApiUrl { get; set; } = "http://127.0.0.1:8642";
    public string ApiKey { get; set; } = "";
    public string Session { get; set; } = "jarvis-delegation";
}

public sealed class MusiqueSettings
{
    public int Secondes { get; set; } = 8;
    public string Source { get; set; } = "loopback"; // loopback (son du PC) | micro
}

public sealed class GestesSettings
{
    public string Token { get; set; } = "";
    public int Device { get; set; } = 0;
    public string HueGroupId { get; set; } = "1";

    // Mapping personnalisé geste → action : "outil" ou "outil:action" ou
    // "outil:action:param1=valeur1;param2=valeur2". Vide = défauts intégrés.
    public Dictionary<string, string> Actions { get; set; } = new();
}

public sealed class PresenceScenesSettings
{
    public string SceneRetour { get; set; } = "";
    public string SceneDepart { get; set; } = "";
}