using JarvisAI.Application.Security;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Goals;

// ── Modèles ─────────────────────────────────────────────────────────────────
public sealed class ObjectifsSettings
{
    public List<Objectif> Items { get; set; } = new();
}

public sealed class Objectif
{
    public string Titre { get; set; } = "";
    public string Details { get; set; } = "";
    /// <summary>actif | termine | abandonne</summary>
    public string Statut { get; set; } = "actif";
    public DateTime Cree { get; set; } = DateTime.UtcNow;
    public DateTime MisAJour { get; set; } = DateTime.UtcNow;
    /// <summary>Journal des actions déjà tentées par le runner.</summary>
    public List<string> Journal { get; set; } = new();
}

// ── Store (JSON atomique, façon RoutinesStore) ──────────────────────────────
public sealed class ObjectifsStore
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "objectifs.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private ObjectifsSettings _settings;

    public ObjectifsStore() => _settings = Load();

    public ObjectifsSettings Get()
    {
        lock (_lock)
            return JsonSerializer.Deserialize<ObjectifsSettings>(
                JsonSerializer.Serialize(_settings, JsonOpts), JsonOpts) ?? new();
    }

    public void Save(ObjectifsSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            JarvisAI.Infrastructure.Security.SafeFileWriter.WriteText(Path_, json);
        }
    }

    private ObjectifsSettings Load()
    {
        if (!System.IO.File.Exists(Path_)) return new ObjectifsSettings();
        try
        {
            return JsonSerializer.Deserialize<ObjectifsSettings>(System.IO.File.ReadAllText(Path_), JsonOpts) ?? new();
        }
        catch
        {
            return new ObjectifsSettings();
        }
    }
}
