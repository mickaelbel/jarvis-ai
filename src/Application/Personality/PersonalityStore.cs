using System.Text.Json;

namespace JarvisAI.Application.Personality;

public sealed class PersonalityOptions
{
    public string Current { get; set; } = "neutre";
}

/// <summary>
/// Personnalités de Jarvis (portage de tools/personnalite.py) : neutre,
/// majordome sarcastique, concis. Persisté dans %LOCALAPPDATA%\JarvisAI.
/// </summary>
public sealed class PersonalityStore
{
    private static readonly Dictionary<string, (string Label, string Guidelines)> Styles =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["neutre"] = ("Neutre",
            "Ton naturel, poli et efficace."),
        ["majordome"] = ("Majordome sarcastique",
            "Tu es un majordome britannique légèrement sarcastique, façon Alfred : tutoiement complice, " +
            "humour pince-sans-rire, petites piques gentilles sur les demandes (« Encore un clavier ? Vos doigts vous remercieront. »), " +
            "mais tu restes TOUJOURS utile et précis : l'information demandée arrive d'abord, la touche d'humour ensuite, en une phrase max."),
        ["concis"] = ("Concis",
            "Réponds en une phrase maximum. Aucun détail superflu, aucune formule de politesse, pas de conseil final.")
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private string _current;

    public PersonalityStore(PersonalityOptions? options = null)
    {
        _current = options?.Current is { Length: > 0 } c && Styles.ContainsKey(c) ? c : "neutre";
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "personality.json");
        try
        {
            if (File.Exists(_filePath))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath));
                if (saved is not null && saved.TryGetValue("current", out var cur) && Styles.ContainsKey(cur))
                    _current = cur;
            }
        }
        catch { /* fichier corrompu : on garde le défaut */ }
    }

    /// <summary>Clé du style actuel (neutre / majordome / concis).</summary>
    public string Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Consignes de style à injecter dans le prompt système.</summary>
    public string CurrentGuidelines
    {
        get
        {
            lock (_lock)
                return Styles.TryGetValue(_current, out var s) ? s.Guidelines : "";
        }
    }

    public static IReadOnlyList<(string Key, string Label)> ListStyles() =>
        Styles.Select(kv => (kv.Key, kv.Value.Label)).ToList();

    public bool TrySet(string key)
    {
        if (!Styles.ContainsKey(key)) return false;
        lock (_lock)
        {
            _current = key.ToLowerInvariant();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["current"] = _current
                }));
            }
            catch { /* persistance best-effort */ }
        }
        return true;
    }
}
