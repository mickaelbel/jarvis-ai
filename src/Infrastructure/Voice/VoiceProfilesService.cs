using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceProfilesService
{
    Task<VoiceProfile> CreateProfileAsync(string name, UserProfileConfig config, CancellationToken ct = default);
    Task<IReadOnlyList<VoiceProfile>> GetProfilesAsync();
    Task<VoiceProfile?> GetProfileAsync(string profileId);
    Task<bool> UpdateProfileAsync(string profileId, UserProfileConfig config);
    Task<bool> DeleteProfileAsync(string profileId);
    Task<bool> SwitchProfileAsync(string profileId);
    Task<VoiceProfile> GetActiveProfileAsync();
    Task<UserProfileConfig> LearnFromInteractionAsync(string profileId, string utterance, string response);
    Task<Dictionary<string, string>> GetVoicePreferencesAsync(string profileId);
}

public sealed class VoiceProfilesService : IVoiceProfilesService
{
    private readonly ILogger<VoiceProfilesService> _logger;
    private readonly string _storagePath;
    private readonly List<VoiceProfile> _profiles = new();
    private string _activeProfileId = "default";

    public VoiceProfilesService(ILogger<VoiceProfilesService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "voice_profiles.json");
        Load();
    }

    public async Task<VoiceProfile> CreateProfileAsync(string name, UserProfileConfig config, CancellationToken ct = default)
    {
        var profile = new VoiceProfile
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Config = config,
            CreatedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow,
            InteractionCount = 0,
            LearnedPhrases = new Dictionary<string, string>()
        };

        _profiles.Add(profile);
        Save();

        _logger.LogInformation("[VoiceProfile] Created: {Name} ({Id})", name, profile.Id);
        return profile;
    }

    public Task<IReadOnlyList<VoiceProfile>> GetProfilesAsync()
    {
        return Task.FromResult<IReadOnlyList<VoiceProfile>>(_profiles.OrderByDescending(p => p.LastUsed).ToList());
    }

    public Task<VoiceProfile?> GetProfileAsync(string profileId)
    {
        return Task.FromResult(_profiles.FirstOrDefault(p => p.Id == profileId));
    }

    public Task<bool> UpdateProfileAsync(string profileId, UserProfileConfig config)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return Task.FromResult(false);

        profile.Config = config;
        profile.ModifiedAt = DateTime.UtcNow;
        Save();
        return Task.FromResult(true);
    }

    public Task<bool> DeleteProfileAsync(string profileId)
    {
        var removed = _profiles.RemoveAll(p => p.Id == profileId) > 0;
        if (removed) Save();
        return Task.FromResult(removed);
    }

    public Task<bool> SwitchProfileAsync(string profileId)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return Task.FromResult(false);

        _activeProfileId = profileId;
        profile.LastUsed = DateTime.UtcNow;
        Save();
        return Task.FromResult(true);
    }

    public Task<VoiceProfile> GetActiveProfileAsync()
    {
        var active = _profiles.FirstOrDefault(p => p.Id == _activeProfileId) ?? _profiles.FirstOrDefault();
        if (active is null)
        {
            // Create default profile
            active = new VoiceProfile
            {
                Id = "default",
                Name = "Défaut",
                Config = new UserProfileConfig(),
                CreatedAt = DateTime.UtcNow
            };
            _profiles.Add(active);
            Save();
        }
        return Task.FromResult(active);
    }

    public Task<UserProfileConfig> LearnFromInteractionAsync(string profileId, string utterance, string response)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return Task.FromResult(new UserProfileConfig());

        // Learn pronunciation preferences
        if (!profile.Config.PreferredPronunciations.ContainsKey(utterance))
        {
            profile.Config.PreferredPronunciations[utterance] = response;
        }

        // Track frequent phrases
        profile.InteractionCount++;
        profile.ModifiedAt = DateTime.UtcNow;
        Save();

        return Task.FromResult(profile.Config);
    }

    public Task<Dictionary<string, string>> GetVoicePreferencesAsync(string profileId)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        return Task.FromResult(profile?.Config.PreferredPronunciations ?? new Dictionary<string, string>());
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<ProfileData>(json);
                if (data is not null)
                {
                    _profiles.AddRange(data.Profiles);
                    _activeProfileId = data.ActiveProfileId;
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new ProfileData
            {
                Profiles = _profiles,
                ActiveProfileId = _activeProfileId
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class VoiceProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public UserProfileConfig Config { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public DateTime? LastUsed { get; set; }
    public int InteractionCount { get; set; }
    public Dictionary<string, string> LearnedPhrases { get; set; } = new();
}

public sealed class UserProfileConfig
{
    public string PreferredVoice { get; set; } = "fr-FR-HenriNeural";
    public float PreferredSpeed { get; set; } = 1.0f;
    public float PreferredVolume { get; set; } = 0.8f;
    public string PreferredLanguage { get; set; } = "fr";
    public string Personality { get; set; } = "neutral";
    public string GreetingStyle { get; set; } = "formal";
    public Dictionary<string, string> PreferredPronunciations { get; set; } = new();
    public List<string> FrequentCommands { get; set; } = new();
    public bool EnableEmotions { get; set; } = true;
    public bool EnableSubtitles { get; set; } = true;
}

internal class ProfileData
{
    public List<VoiceProfile> Profiles { get; set; } = new();
    public string ActiveProfileId { get; set; } = "default";
}
