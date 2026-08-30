using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IUserProfileService
{
    IReadOnlyList<UserProfile> GetProfiles();
    UserProfile? GetProfile(string profileId);
    UserProfile? GetActiveProfile();
    string CreateProfile(string name, string? avatar = null);
    void UpdateProfile(string profileId, string? name = null, string? avatar = null);
    void DeleteProfile(string profileId);
    void SetActiveProfile(string profileId);
    UserPreferences GetPreferences(string profileId);
    void UpdatePreferences(string profileId, UserPreferences preferences);
    IReadOnlyList<UserActivity> GetActivity(string profileId, int days = 7);
}

public sealed class UserProfileService : IUserProfileService
{
    private readonly ILogger<UserProfileService> _logger;
    private readonly string _storagePath;
    private readonly List<UserProfile> _profiles = new();
    private string? _activeProfileId;

    public UserProfileService(ILogger<UserProfileService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "user_profiles.json");
        Load();
    }

    public IReadOnlyList<UserProfile> GetProfiles()
        => _profiles.OrderByDescending(p => p.LastActive).ToList();

    public UserProfile? GetProfile(string profileId)
        => _profiles.FirstOrDefault(p => p.Id == profileId);

    public UserProfile? GetActiveProfile()
        => _activeProfileId is not null ? GetProfile(_activeProfileId) : _profiles.FirstOrDefault();

    public string CreateProfile(string name, string? avatar = null)
    {
        var profile = new UserProfile
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Avatar = avatar ?? "👤",
            CreatedAt = DateTime.UtcNow,
            LastActive = DateTime.UtcNow,
            Preferences = new UserPreferences()
        };

        _profiles.Add(profile);

        if (_profiles.Count == 1)
            _activeProfileId = profile.Id;

        Save();
        return profile.Id;
    }

    public void UpdateProfile(string profileId, string? name = null, string? avatar = null)
    {
        var profile = GetProfile(profileId);
        if (profile is null) return;

        if (name is not null) profile.Name = name;
        if (avatar is not null) profile.Avatar = avatar;
        profile.LastModified = DateTime.UtcNow;
        Save();
    }

    public void DeleteProfile(string profileId)
    {
        _profiles.RemoveAll(p => p.Id == profileId);
        if (_activeProfileId == profileId)
            _activeProfileId = _profiles.FirstOrDefault()?.Id;
        Save();
    }

    public void SetActiveProfile(string profileId)
    {
        if (_profiles.Any(p => p.Id == profileId))
        {
            _activeProfileId = profileId;
            Save();
        }
    }

    public UserPreferences GetPreferences(string profileId)
        => GetProfile(profileId)?.Preferences ?? new UserPreferences();

    public void UpdatePreferences(string profileId, UserPreferences preferences)
    {
        var profile = GetProfile(profileId);
        if (profile is not null)
        {
            profile.Preferences = preferences;
            profile.LastModified = DateTime.UtcNow;
            Save();
        }
    }

    public IReadOnlyList<UserActivity> GetActivity(string profileId, int days = 7)
    {
        var profile = GetProfile(profileId);
        if (profile is null) return new List<UserActivity>();

        return profile.RecentActivity
            .Where(a => a.Timestamp >= DateTime.UtcNow.AddDays(-days))
            .OrderByDescending(a => a.Timestamp)
            .ToList();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("profiles", out var profilesEl))
                {
                    var profilesJson = profilesEl.GetRawText();
                    var loaded = JsonSerializer.Deserialize<List<UserProfile>>(profilesJson);
                    if (loaded is not null) _profiles.AddRange(loaded);
                }

                if (root.TryGetProperty("activeId", out var activeEl))
                {
                    _activeProfileId = activeEl.GetString();
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

            var data = new { profiles = _profiles, activeId = _activeProfileId };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class UserProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "👤";
    public UserPreferences Preferences { get; set; } = new();
    public List<UserActivity> RecentActivity { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime LastActive { get; set; }
    public DateTime? LastModified { get; set; }
}

public sealed class UserPreferences
{
    public string Theme { get; set; } = "dark";
    public string Language { get; set; } = "fr";
    public string Voice { get; set; } = "fr-FR-DeniseNeural";
    public bool NotificationsEnabled { get; set; } = true;
    public bool SoundEnabled { get; set; } = true;
    public double Volume { get; set; } = 0.8;
}

public sealed class UserActivity
{
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
