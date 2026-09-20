using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// Represents a saved voice clone profile.
/// </summary>
public sealed class VoiceCloneProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string ReferenceAudioPath { get; set; } = "";
    public string Language { get; set; } = "french";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; }
}

/// <summary>
/// Manages voice clone profiles: save, load, delete, activate.
/// Profiles are stored in %LOCALAPPDATA%/JarvisAI/voice-clone/
/// </summary>
public sealed class VoiceCloneStore
{
    private readonly string _dir;
    private readonly string _profilesPath;
    private readonly object _lock = new();

    public VoiceCloneStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "voice-clone");
        Directory.CreateDirectory(_dir);
        _profilesPath = Path.Combine(_dir, "profiles.json");
    }

    public string ProfilesDir => _dir;

    public IReadOnlyList<VoiceCloneProfile> LoadAll()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_profilesPath))
                {
                    var json = File.ReadAllText(_profilesPath);
                    var list = JsonSerializer.Deserialize<List<VoiceCloneProfile>>(json) ?? new();
                    return list;
                }
            }
            catch { }
            return new List<VoiceCloneProfile>();
        }
    }

    public VoiceCloneProfile? GetActive()
    {
        lock (_lock)
        {
            return LoadAll().FirstOrDefault(p => p.IsActive);
        }
    }

    public void Save(VoiceCloneProfile profile)
    {
        lock (_lock)
        {
            var list = LoadAll().ToList();
            var idx = list.FindIndex(p => p.Id == profile.Id);
            if (idx >= 0) list[idx] = profile;
            else list.Add(profile);
            Persist(list);
        }
    }

    public bool Activate(string id)
    {
        lock (_lock)
        {
            var list = LoadAll().ToList();
            foreach (var p in list) p.IsActive = p.Id == id;
            Persist(list);
            return list.Any(p => p.Id == id && p.IsActive);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var list = LoadAll().ToList();
            var removed = list.RemoveAll(p => p.Id == id);
            if (removed > 0)
            {
                // Also delete the audio file
                var profile = list.FirstOrDefault(p => p.Id == id);
                if (profile is null)
                {
                    var audioPath = Path.Combine(_dir, $"{id}.wav");
                    if (File.Exists(audioPath)) File.Delete(audioPath);
                }
                Persist(list);
            }
            return removed > 0;
        }
    }

    private void Persist(List<VoiceCloneProfile> list)
    {
        try
        {
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_profilesPath, json);
        }
        catch { }
    }
}
