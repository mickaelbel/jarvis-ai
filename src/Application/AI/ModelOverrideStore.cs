using System.Text.Json;

namespace JarvisAI.Application.AI;

/// <summary>
/// Surcharge persistante des modèles du routeur, choisie par l'utilisateur
/// via l'outil <c>changer_modele</c>. L'IA de base peut proposer n'importe
/// quel modèle Ollama (installé ou à télécharger) ; le choix survit au
/// redémarrage dans %LOCALAPPDATA%\JarvisAI\model-router.json.
/// </summary>
public sealed class ModelOverrideStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _path;

    public string? FastOverride { get; private set; }
    public string? ReasoningOverride { get; private set; }

    public ModelOverrideStore() : this(DefaultPath) { }

    public ModelOverrideStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "model-router.json");

    /// <summary>Charge l'override persisté ; échecs silencieux (fichier absent
    /// ou corrompu = aucun override).</summary>
    public static ModelOverrideStore Load(string? path = null)
    {
        var store = new ModelOverrideStore(path ?? DefaultPath);
        try
        {
            if (!File.Exists(store._path)) return store;
            using var doc = JsonDocument.Parse(File.ReadAllText(store._path));
            store.FastOverride = NullIfEmpty(ReadString(doc.RootElement, "fastModel"));
            store.ReasoningOverride = NullIfEmpty(ReadString(doc.RootElement, "reasoningModel"));
        }
        catch { }
        return store;
    }

    /// <summary>Définit les modèles effectifs (null = revenir aux défauts).</summary>
    public void Set(string? fastModel, string? reasoningModel)
    {
        FastOverride = NullIfEmpty(fastModel);
        ReasoningOverride = NullIfEmpty(reasoningModel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new
            {
                fastModel = FastOverride,
                reasoningModel = ReasoningOverride
            }, JsonOpts));
        }
        catch { }
    }

    public void Reset() => Set(null, null);

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
