using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.WebAutomation;

/// <summary>
/// Enregistre les actions browser (navigation, clics, saisie) et peut les
/// rejouer. Utile pour les séquences répétitives (ex: vérifier une page
/// tous les jours). Les enregistrements sont persistés dans %LOCALAPPDATA%.
/// </summary>
public sealed class BrowserActionRecorder
{
    private readonly ILogger<BrowserActionRecorder> _logger;
    private static readonly string RecordsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JarvisAI", "browser-records");
    private List<BrowserAction>? _currentRecording;
    private string? _currentName;

    public bool IsRecording => _currentRecording is not null;

    public BrowserActionRecorder(ILogger<BrowserActionRecorder> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(RecordsDir);
    }

    public void StartRecording(string name)
    {
        _currentRecording = new List<BrowserAction>();
        _currentName = name;
        _logger.LogInformation("[BrowserRecord] Démarrage enregistrement: {Name}", name);
    }

    public void RecordNavigation(string url)
    {
        _currentRecording?.Add(new BrowserAction { Type = "navigate", Url = url, Timestamp = DateTime.UtcNow });
    }

    public void RecordClick(string selector)
    {
        _currentRecording?.Add(new BrowserAction { Type = "click", Selector = selector, Timestamp = DateTime.UtcNow });
    }

    public void RecordType(string selector, string text)
    {
        _currentRecording?.Add(new BrowserAction { Type = "type", Selector = selector, Text = text, Timestamp = DateTime.UtcNow });
    }

    public void RecordWait(int milliseconds)
    {
        _currentRecording?.Add(new BrowserAction { Type = "wait", WaitMs = milliseconds, Timestamp = DateTime.UtcNow });
    }

    public string? StopRecording()
    {
        if (_currentRecording is null || _currentName is null) return null;
        var json = JsonSerializer.Serialize(_currentRecording, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(RecordsDir, $"{_currentName}.json");
        File.WriteAllText(path, json);
        _logger.LogInformation("[BrowserRecord] Enregistrement sauvegardé: {Path} ({Count} actions)", path, _currentRecording.Count);
        var name = _currentName;
        _currentRecording = null;
        _currentName = null;
        return name;
    }

    public IReadOnlyList<string> ListRecordings()
    {
        return Directory.GetFiles(RecordsDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .ToList()
            .AsReadOnly();
    }

    public List<BrowserAction>? LoadRecording(string name)
    {
        var path = Path.Combine(RecordsDir, $"{name}.json");
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<BrowserAction>>(json);
    }

    public bool DeleteRecording(string name)
    {
        var path = Path.Combine(RecordsDir, $"{name}.json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}

public sealed class BrowserAction
{
    public string Type { get; set; } = "";
    public string? Url { get; set; }
    public string? Selector { get; set; }
    public string? Text { get; set; }
    public int WaitMs { get; set; }
    public DateTime Timestamp { get; set; }
}
