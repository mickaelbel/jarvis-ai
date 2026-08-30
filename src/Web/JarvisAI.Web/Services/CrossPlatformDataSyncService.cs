using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ICrossPlatformDataSyncService
{
    SyncProfile CreateProfile(string deviceName, string platform);
    IReadOnlyList<SyncProfile> GetProfiles();
    SyncResult SyncData(string profileId, SyncDirection direction);
    IReadOnlyList<SyncItem> GetSyncQueue(string profileId);
    void AddToQueue(string profileId, string dataType, string key, string value);
    void ClearQueue(string profileId);
    DateTime? GetLastSyncTime(string profileId);
    string ExportData(string profileId, string format = "json");
}

public sealed class CrossPlatformDataSyncService : ICrossPlatformDataSyncService
{
    private readonly ILogger<CrossPlatformDataSyncService> _logger;
    private readonly string _storagePath;
    private readonly List<SyncProfile> _profiles = new();
    private readonly Dictionary<string, List<SyncItem>> _queues = new();
    private readonly Dictionary<string, string> _data = new();

    public CrossPlatformDataSyncService(ILogger<CrossPlatformDataSyncService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "data_sync.json");
        Load();
    }

    public SyncProfile CreateProfile(string deviceName, string platform)
    {
        var profile = new SyncProfile
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            DeviceName = deviceName,
            Platform = platform,
            CreatedAt = DateTime.UtcNow,
            LastSync = null
        };

        _profiles.Add(profile);
        _queues[profile.Id] = new List<SyncItem>();
        Save();

        _logger.LogInformation("[DataSync] Created profile: {Device} ({Platform})", deviceName, platform);
        return profile;
    }

    public IReadOnlyList<SyncProfile> GetProfiles()
        => _profiles.OrderByDescending(p => p.LastSync ?? DateTime.MinValue).ToList();

    public SyncResult SyncData(string profileId, SyncDirection direction)
    {
        var queue = _queues.TryGetValue(profileId, out var q) ? q : new List<SyncItem>();
        var result = new SyncResult
        {
            ProfileId = profileId,
            Direction = direction,
            StartedAt = DateTime.UtcNow,
            ItemsProcessed = 0,
            ItemsSkipped = 0
        };

        try
        {
            foreach (var item in queue.Where(i => !i.Synced))
            {
                var key = $"{profileId}:{item.DataType}:{item.Key}";

                if (direction == SyncDirection.Push || direction == SyncDirection.Both)
                {
                    _data[key] = item.Value;
                    result.ItemsProcessed++;
                }
                else if (direction == SyncDirection.Pull && _data.ContainsKey(key))
                {
                    item.Value = _data[key];
                    result.ItemsProcessed++;
                }
                else
                {
                    result.ItemsSkipped++;
                }

                item.Synced = true;
                item.SyncedAt = DateTime.UtcNow;
            }

            result.Status = "completed";
            result.CompletedAt = DateTime.UtcNow;

            // Update last sync time
            var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile is not null)
                profile.LastSync = DateTime.UtcNow;

            Save();
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public IReadOnlyList<SyncItem> GetSyncQueue(string profileId)
        => _queues.TryGetValue(profileId, out var q) ? q.OrderByDescending(i => i.CreatedAt).ToList() : new List<SyncItem>();

    public void AddToQueue(string profileId, string dataType, string key, string value)
    {
        if (!_queues.ContainsKey(profileId))
            _queues[profileId] = new List<SyncItem>();

        _queues[profileId].Add(new SyncItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            DataType = dataType,
            Key = key,
            Value = value,
            CreatedAt = DateTime.UtcNow
        });

        Save();
    }

    public void ClearQueue(string profileId)
    {
        if (_queues.ContainsKey(profileId))
            _queues[profileId].Clear();
        Save();
    }

    public DateTime? GetLastSyncTime(string profileId)
        => _profiles.FirstOrDefault(p => p.Id == profileId)?.LastSync;

    public string ExportData(string profileId, string format = "json")
    {
        var items = _queues.TryGetValue(profileId, out var q) ? q : new List<SyncItem>();
        return format switch
        {
            "json" => JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }),
            "csv" => string.Join("\n", items.Select(i => $"{i.DataType},{i.Key},{i.Value}")),
            _ => string.Join("\n", items.Select(i => $"{i.DataType}: {i.Key} = {i.Value}"))
        };
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
                    var loaded = JsonSerializer.Deserialize<List<SyncProfile>>(profilesEl.GetRawText());
                    if (loaded is not null) _profiles.AddRange(loaded);
                }

                if (root.TryGetProperty("queues", out var queuesEl))
                {
                    foreach (var prop in queuesEl.EnumerateObject())
                    {
                        var loaded = JsonSerializer.Deserialize<List<SyncItem>>(prop.Value.GetRawText());
                        if (loaded is not null) _queues[prop.Name] = loaded;
                    }
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

            var data = new { profiles = _profiles, queues = _queues };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class SyncProfile
{
    public string Id { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSync { get; set; }
}

public sealed class SyncItem
{
    public string Id { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Synced { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SyncedAt { get; set; }
}

public sealed class SyncResult
{
    public string ProfileId { get; set; } = "";
    public SyncDirection Direction { get; set; }
    public string Status { get; set; } = "";
    public int ItemsProcessed { get; set; }
    public int ItemsSkipped { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum SyncDirection
{
    Push,
    Pull,
    Both
}
