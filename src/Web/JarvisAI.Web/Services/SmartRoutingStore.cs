using System.IO;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public sealed class SmartRoutingSettings
{
    public bool Enabled { get; set; } = true;
    public bool VoiceEnabled { get; set; } = true;
    public bool MultiStepEnabled { get; set; } = true;
}

/// <summary>
/// Persiste les réglages de routage intelligent (activé/désactivé) dans un
/// fichier JSON local pour qu'ils survivent aux redémarrages.
/// </summary>
public sealed class SmartRoutingStore
{
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "smart-routing.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private SmartRoutingSettings _settings;

    public SmartRoutingStore()
    {
        _settings = Load();
    }

    public SmartRoutingSettings Get()
    {
        lock (_lock) return Clone(_settings);
    }

    public void Save(SmartRoutingSettings settings)
    {
        lock (_lock)
        {
            _settings = Clone(settings);
            Persist(_settings);
        }
    }

    private static SmartRoutingSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<SmartRoutingSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
        }
        return new SmartRoutingSettings();
    }

    private static void Persist(SmartRoutingSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
        }
    }

    private static SmartRoutingSettings Clone(SmartRoutingSettings s) => new()
    {
        Enabled = s.Enabled,
        VoiceEnabled = s.VoiceEnabled,
        MultiStepEnabled = s.MultiStepEnabled
    };
}
