using JarvisAI.Application.Memory;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Memory;

public sealed class MemorySettingsStore : IMemorySettingsStore
{
    private readonly ILogger<MemorySettingsStore> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private MemorySettings? _cached;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public MemorySettingsStore(ILogger<MemorySettingsStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI",
            "memory-settings.json");
    }

    public MemorySettings Get()
    {
        lock (_lock)
        {
            if (_cached is not null) return Clone(_cached);

            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var loaded = JsonSerializer.Deserialize<MemorySettings>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        _cached = loaded;
                        return Clone(loaded);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MemorySettings] Failed to load from {Path}", _filePath);
            }

            _cached = new MemorySettings();
            return Clone(_cached);
        }
    }

    public void Save(MemorySettings settings)
    {
        lock (_lock)
        {
            _cached = Clone(settings);
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MemorySettings] Failed to save to {Path}", _filePath);
            }
        }
    }

    private static MemorySettings Clone(MemorySettings s) => new()
    {
        MemoryEnabled = s.MemoryEnabled,
        RecordEpisodes = s.RecordEpisodes,
        AutoSaveFacts = s.AutoSaveFacts,
        MaxEpisodesDays = s.MaxEpisodesDays,
        MaxLongTermMemories = s.MaxLongTermMemories
    };
}
