using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IVoiceCommandCustomizerService
{
    VoiceCommand CreateCommand(string trigger, string action, string? description = null);
    IReadOnlyList<VoiceCommand> GetCommands();
    VoiceCommand? GetCommand(string commandId);
    void UpdateCommand(string commandId, string? trigger = null, string? action = null);
    void DeleteCommand(string commandId);
    void ToggleCommand(string commandId, bool enabled);
    IReadOnlyList<VoiceCommand> GetEnabledCommands();
    string MatchCommand(string spokenText);
    IReadOnlyList<VoiceCommandCategory> GetCategories();
}

public sealed class VoiceCommandCustomizerService : IVoiceCommandCustomizerService
{
    private readonly ILogger<VoiceCommandCustomizerService> _logger;
    private readonly string _storagePath;
    private readonly List<VoiceCommand> _commands = new();

    public VoiceCommandCustomizerService(ILogger<VoiceCommandCustomizerService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "voice_commands.json");
        Load();
        InitializeDefaults();
    }

    public VoiceCommand CreateCommand(string trigger, string action, string? description = null)
    {
        var command = new VoiceCommand
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Trigger = trigger.ToLowerInvariant(),
            Action = action,
            Description = description ?? "",
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _commands.Add(command);
        Save();

        _logger.LogInformation("[VoiceCmd] Created: {Trigger} -> {Action}", trigger, action);
        return command;
    }

    public IReadOnlyList<VoiceCommand> GetCommands()
        => _commands.OrderByDescending(c => c.CreatedAt).ToList();

    public VoiceCommand? GetCommand(string commandId)
        => _commands.FirstOrDefault(c => c.Id == commandId);

    public void UpdateCommand(string commandId, string? trigger = null, string? action = null)
    {
        var command = _commands.FirstOrDefault(c => c.Id == commandId);
        if (command is not null)
        {
            if (trigger is not null) command.Trigger = trigger.ToLowerInvariant();
            if (action is not null) command.Action = action;
            command.ModifiedAt = DateTime.UtcNow;
            Save();
        }
    }

    public void DeleteCommand(string commandId)
    {
        _commands.RemoveAll(c => c.Id == commandId);
        Save();
    }

    public void ToggleCommand(string commandId, bool enabled)
    {
        var command = _commands.FirstOrDefault(c => c.Id == commandId);
        if (command is not null)
        {
            command.Enabled = enabled;
            command.ModifiedAt = DateTime.UtcNow;
            Save();
        }
    }

    public IReadOnlyList<VoiceCommand> GetEnabledCommands()
        => _commands.Where(c => c.Enabled).ToList();

    public string MatchCommand(string spokenText)
    {
        var normalized = spokenText.ToLowerInvariant().Trim();

        // Exact match
        var exact = _commands.FirstOrDefault(c => c.Enabled && c.Trigger == normalized);
        if (exact is not null) return exact.Action;

        // Partial match
        var partial = _commands.FirstOrDefault(c =>
            c.Enabled && (normalized.Contains(c.Trigger) || c.Trigger.Contains(normalized)));

        return partial?.Action ?? "";
    }

    public IReadOnlyList<VoiceCommandCategory> GetCategories()
    {
        return _commands
            .GroupBy(c => c.Category)
            .Select(g => new VoiceCommandCategory
            {
                Name = g.Key,
                Commands = g.ToList(),
                Count = g.Count()
            })
            .OrderByDescending(c => c.Count)
            .ToList();
    }

    private void InitializeDefaults()
    {
        if (_commands.Count > 0) return;

        var defaults = new[]
        {
            ("ouvre blender", "open_app:blender", "Ouvrir Blender"),
            ("ouvre chrome", "open_app:chrome", "Ouvrir Chrome"),
            ("arrête tout", "shutdown:pc", "Arrêter l'ordinateur"),
            ("nouveau screenshot", "screenshot:capture", "Capturer l'écran"),
            ("mute", "volume:mute", "Couper le son"),
            ("unmute", "volume:unmute", "Remettre le son"),
            ("next track", "media:next", "Piste suivante"),
            ("previous track", "media:previous", "Piste précédente")
        };

        foreach (var (trigger, action, desc) in defaults)
        {
            _commands.Add(new VoiceCommand
            {
                Id = Guid.NewGuid().ToString("N")[8..],
                Trigger = trigger,
                Action = action,
                Description = desc,
                Category = "System",
                Enabled = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<VoiceCommand>>(json);
                if (loaded is not null) _commands.AddRange(loaded);
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

            var json = JsonSerializer.Serialize(_commands, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class VoiceCommand
{
    public string Id { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Action { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "General";
    public bool Enabled { get; set; }
    public int UsageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public sealed class VoiceCommandCategory
{
    public string Name { get; set; } = "";
    public List<VoiceCommand> Commands { get; set; } = new();
    public int Count { get; set; }
}
