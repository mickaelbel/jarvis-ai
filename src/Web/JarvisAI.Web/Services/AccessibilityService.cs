using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IAccessibilityService
{
    AccessibilitySettings GetSettings();
    void UpdateSettings(AccessibilitySettings settings);
    IReadOnlyList<string> GetAriaLabels(string elementId);
    string GetAriaDescription(string elementType, string? context = null);
    bool IsScreenReaderActive();
    void AnnounceScreenReaderMessage(string message);
}

public sealed class AccessibilityService : IAccessibilityService
{
    private readonly ILogger<AccessibilityService> _logger;
    private readonly string _storagePath;
    private AccessibilitySettings _settings = new();

    private static readonly Dictionary<string, string> DefaultLabels = new()
    {
        ["chat-input"] = "Zone de texte du chat - Tapez votre message ici",
        ["send-button"] = "Envoyer le message",
        ["voice-button"] = "Activer le microphone pour commandes vocales",
        ["settings-button"] = "Ouvrir les paramètres",
        ["overlay-toggle"] = "Afficher ou masquer l'overlay",
        ["new-chat"] = "Nouvelle conversation",
        ["theme-toggle"] = "Basculer entre mode sombre et clair",
        ["volume-slider"] = "Volume de la synthèse vocale",
        ["model-selector"] = "Sélectionner le modèle d'IA",
        ["file-upload"] = "Télécharger un fichier",
        ["memory-search"] = "Rechercher dans la mémoire",
        ["clipboard-copy"] = "Copier dans le presse-papiers",
        ["undo-button"] = "Annuler la dernière action",
        ["redo-button"] = "Rétablir l'action annulée",
        ["notification-dismiss"] = "Fermer la notification",
        ["close-dialog"] = "Fermer la boîte de dialogue",
    };

    public AccessibilityService(ILogger<AccessibilityService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "accessibility.json");
        Load();
    }

    public AccessibilitySettings GetSettings() => _settings;

    public void UpdateSettings(AccessibilitySettings settings)
    {
        _settings = settings;
        Save();
        _logger.LogInformation("[Accessibility] Settings updated: ScreenReader={SR}, HighContrast={HC}, ReducedMotion={RM}",
            settings.ScreenReaderSupport, settings.HighContrast, settings.ReducedMotion);
    }

    public IReadOnlyList<string> GetAriaLabels(string elementId)
    {
        var labels = new List<string>();
        if (DefaultLabels.TryGetValue(elementId, out var label))
            labels.Add(label);
        return labels;
    }

    public string GetAriaDescription(string elementType, string? context = null)
    {
        return elementType switch
        {
            "chat-message" => $"Message {context ?? ""}",
            "tool-result" => $"Résultat de l'outil: {context}",
            "error" => $"Erreur: {context}",
            "success" => $"Succès: {context}",
            _ => context ?? elementType
        };
    }

    public bool IsScreenReaderActive() => _settings.ScreenReaderSupport;

    public void AnnounceScreenReaderMessage(string message)
    {
        _logger.LogDebug("[Accessibility] Screen reader announce: {Message}", message);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                _settings = JsonSerializer.Deserialize<AccessibilitySettings>(json) ?? new();
            }
        }
        catch { _settings = new(); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class AccessibilitySettings
{
    public bool ScreenReaderSupport { get; set; } = true;
    public bool HighContrast { get; set; }
    public bool ReducedMotion { get; set; }
    public bool LargeText { get; set; }
    public double FontScale { get; set; } = 1.0;
    public bool KeyboardNavigation { get; set; } = true;
    public bool FocusIndicators { get; set; } = true;
    public bool AudioCues { get; set; } = true;
    public string PreferredLanguage { get; set; } = "fr";
}
