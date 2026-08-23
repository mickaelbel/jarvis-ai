using System.Text.Json;

namespace JarvisAI.Web.Services;

/// <summary>
/// Dossier de téléchargement des modèles Ollama. La source de vérité pour
/// Ollama est la variable d'environnement utilisateur OLLAMA_MODELS ; ce store
/// conserve un doublon JSON pour que l'application sache où les modèles se
/// trouvent même après un redémarrage.
/// </summary>
public sealed class OllamaModelsStore
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private string? _modelsPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public OllamaModelsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI",
            "ollama-models.json");
        Load();
    }

    public event EventHandler? Changed;

    public string? ModelsPath
    {
        get { lock (_lock) return _modelsPath; }
        set
        {
            lock (_lock)
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (string.Equals(_modelsPath, normalized, StringComparison.OrdinalIgnoreCase)) return;
                _modelsPath = normalized;
                Save();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public static string? UserEnvModelsPath() =>
        Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User)?.Trim();

    public static string DefaultModelsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");

    /// <summary>Le dossier qui sera réellement utilisé par Ollama au prochain démarrage.</summary>
    public string EffectivePath()
    {
        var env = UserEnvModelsPath();
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return !string.IsNullOrWhiteSpace(ModelsPath) ? ModelsPath : DefaultModelsPath();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<OllamaModelsData>(json, JsonOptions);
            _modelsPath = string.IsNullOrWhiteSpace(data?.Path) ? null : data.Path.Trim();
        }
        catch
        {
            // un fichier de configuration corrompu ne doit jamais bloquer l'application
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new OllamaModelsData { Path = _modelsPath }, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // échec d'écriture : on continue sans persister
        }
    }

    private sealed class OllamaModelsData
    {
        public string? Path { get; set; }
    }
}
