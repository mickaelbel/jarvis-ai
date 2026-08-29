using System.Text.Json;

namespace JarvisAI.Application.Services;

/// <summary>
/// Gère les profils de configuration (travail/personnel) et l'export/import
/// de la configuration au format JSON.
/// </summary>
public sealed class SettingsProfileService
{
    private static readonly string ProfilesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "profiles");

    private static readonly string ActiveProfileFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "active-profile.txt");

    public string ActiveProfile { get; private set; } = "default";

    public SettingsProfileService()
    {
        Directory.CreateDirectory(ProfilesDir);
        if (File.Exists(ActiveProfileFile))
            ActiveProfile = File.ReadAllText(ActiveProfileFile).Trim();
    }

    public IReadOnlyList<string> ListProfiles()
    {
        var profiles = Directory.GetFiles(ProfilesDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToList();
        if (!profiles.Contains("default"))
            profiles.Insert(0, "default");
        return profiles.AsReadOnly();
    }

    public void SaveProfile(string name, string jsonConfig)
    {
        var path = GetProfilePath(name);
        File.WriteAllText(path, jsonConfig);
    }

    public string? LoadProfile(string name)
    {
        var path = GetProfilePath(name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public bool DeleteProfile(string name)
    {
        if (name == "default") return false;
        var path = GetProfilePath(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public void SetActive(string name)
    {
        ActiveProfile = name;
        File.WriteAllText(ActiveProfileFile, name);
    }

    public string ExportToJson(object settings)
    {
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    public T? ImportFromJson<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json);
    }

    private string GetProfilePath(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(ProfilesDir, $"{safe}.json");
    }
}
