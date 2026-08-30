using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IEnvironmentManagerService
{
    IReadOnlyList<EnvironmentConfig> GetEnvironments();
    EnvironmentConfig? GetEnvironment(string envId);
    string CreateEnvironment(string name, EnvironmentType type, string? description = null);
    void UpdateEnvironment(string envId, Dictionary<string, string>? variables = null, string? description = null);
    void DeleteEnvironment(string envId);
    void SetActiveEnvironment(string envId);
    EnvironmentConfig? GetActiveEnvironment();
    IReadOnlyList<EnvironmentVariable> GetVariables(string envId);
    string ExportEnvironment(string envId, string format = "json");
}

public sealed class EnvironmentManagerService : IEnvironmentManagerService
{
    private readonly ILogger<EnvironmentManagerService> _logger;
    private readonly string _storagePath;
    private readonly List<EnvironmentConfig> _environments = new();
    private string? _activeEnvironmentId;

    public EnvironmentManagerService(ILogger<EnvironmentManagerService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "environments.json");
        Load();
    }

    public IReadOnlyList<EnvironmentConfig> GetEnvironments()
        => _environments.OrderByDescending(e => e.LastModified ?? e.CreatedAt).ToList();

    public EnvironmentConfig? GetEnvironment(string envId)
        => _environments.FirstOrDefault(e => e.Id == envId);

    public string CreateEnvironment(string name, EnvironmentType type, string? description = null)
    {
        var env = new EnvironmentConfig
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Type = type,
            Description = description ?? "",
            Variables = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };

        _environments.Add(env);
        Save();
        return env.Id;
    }

    public void UpdateEnvironment(string envId, Dictionary<string, string>? variables = null, string? description = null)
    {
        var env = GetEnvironment(envId);
        if (env is null) return;

        if (variables is not null)
        {
            foreach (var kv in variables)
                env.Variables[kv.Key] = kv.Value;
        }

        if (description is not null)
            env.Description = description;

        env.LastModified = DateTime.UtcNow;
        Save();
    }

    public void DeleteEnvironment(string envId)
    {
        _environments.RemoveAll(e => e.Id == envId);
        if (_activeEnvironmentId == envId)
            _activeEnvironmentId = _environments.FirstOrDefault()?.Id;
        Save();
    }

    public void SetActiveEnvironment(string envId)
    {
        if (_environments.Any(e => e.Id == envId))
        {
            _activeEnvironmentId = envId;
            Save();
        }
    }

    public EnvironmentConfig? GetActiveEnvironment()
        => _activeEnvironmentId is not null ? GetEnvironment(_activeEnvironmentId) : _environments.FirstOrDefault();

    public IReadOnlyList<EnvironmentVariable> GetVariables(string envId)
    {
        var env = GetEnvironment(envId);
        if (env is null) return new List<EnvironmentVariable>();

        return env.Variables.Select(kv => new EnvironmentVariable
        {
            Key = kv.Key,
            Value = kv.Value,
            IsSecret = kv.Key.Contains("SECRET") || kv.Key.Contains("PASSWORD") || kv.Key.Contains("KEY")
        }).ToList();
    }

    public string ExportEnvironment(string envId, string format = "json")
    {
        var env = GetEnvironment(envId);
        if (env is null) return "{}";

        return format.ToLowerInvariant() switch
        {
            "json" => JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true }),
            "env" => string.Join("\n", env.Variables.Select(kv => $"{kv.Key}={kv.Value}")),
            _ => "{}"
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

                if (root.TryGetProperty("environments", out var envsEl))
                {
                    var envsJson = envsEl.GetRawText();
                    var loaded = JsonSerializer.Deserialize<List<EnvironmentConfig>>(envsJson);
                    if (loaded is not null) _environments.AddRange(loaded);
                }

                if (root.TryGetProperty("activeId", out var activeEl))
                {
                    _activeEnvironmentId = activeEl.GetString();
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

            var data = new { environments = _environments, activeId = _activeEnvironmentId };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class EnvironmentConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public EnvironmentType Type { get; set; }
    public string Description { get; set; } = "";
    public Dictionary<string, string> Variables { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModified { get; set; }
}

public sealed class EnvironmentVariable
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
}

public enum EnvironmentType { Development, Staging, Production, Testing }
