using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Security;

public interface IPersonaManager
{
    IReadOnlyList<Persona> GetPersonas();
    Persona? GetActivePersona();
    void SetActivePersona(string personaId);
    string CreatePersona(string name, string systemPrompt, string? description = null);
    void DeletePersona(string personaId);
    void UpdatePersona(string personaId, string name, string systemPrompt, string? description = null);
}

public sealed class PersonaManager : IPersonaManager
{
    private readonly ILogger<PersonaManager> _logger;
    private readonly string _storagePath;
    private readonly List<Persona> _personas = new();
    private string _activePersonaId = "default";

    public PersonaManager(ILogger<PersonaManager> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "personas.json");
        Load();
        EnsureDefaultPersona();
    }

    private void EnsureDefaultPersona()
    {
        if (_personas.All(p => p.Id != "default"))
        {
            _personas.Insert(0, new Persona
            {
                Id = "default",
                Name = "Jarvis",
                SystemPrompt = "Tu es Jarvis, un assistant IA avancé. Tu réponds en français, tu es précis et utile.",
                Description = "Personnalité par défaut de Jarvis"
            });
        }
    }

    public IReadOnlyList<Persona> GetPersonas() => _personas.ToList();

    public Persona? GetActivePersona()
    {
        return _personas.FirstOrDefault(p => p.Id == _activePersonaId)
            ?? _personas.FirstOrDefault();
    }

    public void SetActivePersona(string personaId)
    {
        if (_personas.Any(p => p.Id == personaId))
        {
            _activePersonaId = personaId;
            Save();
            _logger.LogInformation("[Persona] Active persona set to: {Id}", personaId);
        }
    }

    public string CreatePersona(string name, string systemPrompt, string? description = null)
    {
        var persona = new Persona
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            SystemPrompt = systemPrompt,
            Description = description ?? "",
            CreatedAt = DateTime.UtcNow
        };

        _personas.Add(persona);
        Save();
        _logger.LogInformation("[Persona] Created: {Name}", name);
        return persona.Id;
    }

    public void DeletePersona(string personaId)
    {
        if (personaId == "default") return;
        _personas.RemoveAll(p => p.Id == personaId);
        if (_activePersonaId == personaId) _activePersonaId = "default";
        Save();
    }

    public void UpdatePersona(string personaId, string name, string systemPrompt, string? description = null)
    {
        var persona = _personas.FirstOrDefault(p => p.Id == personaId);
        if (persona is null) return;

        persona.Name = name;
        persona.SystemPrompt = systemPrompt;
        persona.Description = description ?? persona.Description;
        persona.LastModified = DateTime.UtcNow;
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<PersonaData>(json);
                if (data is not null)
                {
                    _personas.AddRange(data.Personas);
                    _activePersonaId = data.ActivePersonaId ?? "default";
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

            var data = new PersonaData { Personas = _personas, ActivePersonaId = _activePersonaId };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class Persona
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastModified { get; set; }
}

internal sealed class PersonaData
{
    public List<Persona> Personas { get; set; } = new();
    public string? ActivePersonaId { get; set; }
}
