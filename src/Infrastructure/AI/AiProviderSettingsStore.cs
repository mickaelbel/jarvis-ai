using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

/// <summary>
/// Persistance des clés API et préférences des fournisseurs IA dans
/// %LOCALAPPDATA%\JarvisAI\ai-providers.json (jamais dans le code source).
/// </summary>
public sealed class AiProviderSettingsStore
{
    private readonly ILogger<AiProviderSettingsStore> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, AiProviderSettings>? _cached;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AiProviderSettingsStore(ILogger<AiProviderSettingsStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI",
            "ai-providers.json");
    }

    public Dictionary<string, AiProviderSettings> Get()
    {
        lock (_lock)
        {
            if (_cached is not null) return Clone(_cached);

            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, AiProviderSettings>>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        _cached = loaded;
                        return Clone(loaded);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AiProviders] Failed to load settings from {Path}", _filePath);
            }

            _cached = new Dictionary<string, AiProviderSettings>();
            return Clone(_cached);
        }
    }

    public void Save(Dictionary<string, AiProviderSettings> settings)
    {
        lock (_lock)
        {
            _cached = Clone(settings);
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(_filePath, JsonSerializer.Serialize(_cached, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AiProviders] Failed to save settings to {Path}", _filePath);
            }
        }
    }

    private static Dictionary<string, AiProviderSettings> Clone(Dictionary<string, AiProviderSettings> source)
    {
        var clone = new Dictionary<string, AiProviderSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            clone[key] = new AiProviderSettings
            {
                Enabled = value.Enabled,
                DisplayName = value.DisplayName ?? "",
                ApiKey = value.ApiKey ?? "",
                BaseUrl = value.BaseUrl ?? "",
                DefaultModel = value.DefaultModel ?? "",
                Models = value.Models?.ToList() ?? new List<string>()
            };
        }
        return clone;
    }
}
