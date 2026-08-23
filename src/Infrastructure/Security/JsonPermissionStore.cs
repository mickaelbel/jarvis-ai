using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisAI.Infrastructure.Security;

/// <summary>
/// Stockage JSON des autorisations « toujours autoriser ». Fichier :
/// %LOCALAPPDATA%\JarvisAI\permissions.json — éditable à la main pour tout révoquer.
/// Thread-safe (lock) ; écriture atomique via fichier temporaire.
/// </summary>
public sealed class JsonPermissionStore : IPermissionStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly ILogger<JsonPermissionStore> _logger;

    public JsonPermissionStore(ILogger<JsonPermissionStore> logger, string? filePath = null)
    {
        _logger = logger;
        var dir = filePath is not null
            ? Path.GetDirectoryName(filePath)!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI");
        Directory.CreateDirectory(dir);
        _filePath = filePath ?? Path.Combine(dir, "permissions.json");
    }

    public Task<bool> IsAlwaysAllowedAsync(string toolName, string? action = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Load().Contains(FormatEntry(toolName, action)));

    public Task<bool> AllowAlwaysAsync(string toolName, string? action = null, CancellationToken cancellationToken = default)
    {
        var entry = FormatEntry(toolName, action);
        bool added = false;
        lock (_lock)
        {
            var entries = Load();
            if (!entries.Contains(entry))
            {
                entries.Add(entry);
                Save(entries);
                added = true;
            }
        }
        if (added) _logger.LogInformation("[Permissions] « Toujours autoriser » mémorisé : {Entry}", entry);
        return Task.FromResult(true);
    }

    public Task<bool> RevokeAsync(string toolName, string? action = null, CancellationToken cancellationToken = default)
    {
        var entry = FormatEntry(toolName, action);
        bool removed = false;
        lock (_lock)
        {
            var entries = Load();
            removed = entries.RemoveAll(e =>
                e.Equals(entry, StringComparison.OrdinalIgnoreCase)
                || e.StartsWith(toolName + ":", StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save(entries);
        }
        if (removed) _logger.LogInformation("[Permissions] Autorisation révoquée : {Tool}", toolName);
        return Task.FromResult(removed);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Load());

    internal static string FormatEntry(string toolName, string? action)
        => string.IsNullOrWhiteSpace(action) ? toolName.ToLowerInvariant() : $"{toolName.ToLowerInvariant()}:{action.ToLowerInvariant()}";

    private List<string> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath)) return new List<string>();
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<PermissionFile>(json);
                return data?.AlwaysAllow?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Permissions] Lecture impossible de {File}, liste vide", _filePath);
                return new List<string>();
            }
        }
    }

    private void Save(List<string> entries)
    {
        try
        {
            var json = JsonSerializer.Serialize(new PermissionFile { AlwaysAllow = entries }, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Permissions] Écriture impossible de {File}", _filePath);
        }
    }

    private sealed class PermissionFile
    {
        [JsonPropertyName("always_allow")]
        public List<string>? AlwaysAllow { get; set; }
    }
}
