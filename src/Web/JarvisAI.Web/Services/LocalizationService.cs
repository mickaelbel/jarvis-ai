using System.Text.Json;

namespace JarvisAI.Web.Services;

/// <summary>
/// Supported UI languages.
/// </summary>
public enum UiLanguage
{
    Fr,
    En
}

/// <summary>
/// Localization service for UI strings. Stores translations in JSON.
/// Supports FR and EN. Default: FR.
/// </summary>
public sealed class LocalizationService
{
    private readonly string _filePath;
    private Dictionary<string, string> _strings = new();
    private UiLanguage _currentLanguage = UiLanguage.Fr;
    private readonly object _lock = new();

    public UiLanguage CurrentLanguage => _currentLanguage;

    public event Action? LanguageChanged;

    public LocalizationService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "localization.json");
        Load();
    }

    public string this[string key] => GetString(key);

    public string GetString(string key)
    {
        lock (_lock)
        {
            return _strings.TryGetValue(key, out var val) ? val : key;
        }
    }

    public void SetLanguage(UiLanguage lang)
    {
        lock (_lock)
        {
            _currentLanguage = lang;
            LoadStringsForLanguage(lang);
            Save();
        }
        LanguageChanged?.Invoke();
    }

    public void SetLanguage(string langCode)
    {
        var lang = langCode?.ToLowerInvariant() switch
        {
            "en" => UiLanguage.En,
            "fr" or _ => UiLanguage.Fr
        };
        SetLanguage(lang);
    }

    public string GetLanguageCode() => _currentLanguage switch
    {
        UiLanguage.En => "en",
        _ => "fr"
    };

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var config = JsonSerializer.Deserialize<LangConfig>(json);
                if (config is not null)
                {
                    _currentLanguage = config.Language;
                    LoadStringsForLanguage(_currentLanguage);
                    return;
                }
            }
        }
        catch { }

        // Default: French
        LoadStringsForLanguage(UiLanguage.Fr);
    }

    private void LoadStringsForLanguage(UiLanguage lang)
    {
        _strings = lang switch
        {
            UiLanguage.En => GetEnglishStrings(),
            _ => GetFrenchStrings()
        };
    }

    private void Save()
    {
        try
        {
            var config = new LangConfig { Language = _currentLanguage };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }

    private sealed class LangConfig
    {
        public UiLanguage Language { get; set; } = UiLanguage.Fr;
    }

    // ═══════════════════════════════════════════════════════════════
    // FRENCH STRINGS
    // ═══════════════════════════════════════════════════════════════
    private static Dictionary<string, string> GetFrenchStrings() => new()
    {
        // Setup
        ["setup.welcome"] = "Bienvenue",
        ["setup.welcome.subtitle"] = "Faisons connaissance en quelques étapes. Rien n'est définitif, tout est modifiable ensuite.",
        ["setup.language"] = "Langue",
        ["setup.language.subtitle"] = "Choisis la langue de l'interface et des réponses de Jarvis.",
        ["setup.language.choose"] = "Langue de l'interface",
        ["setup.model"] = "Intelligence artificielle",
        ["setup.model.subtitle"] = "Vérifions le moteur et le modèle qui te répondront.",
        ["setup.voice"] = "Voix",
        ["setup.voice.subtitle"] = "Microphone, haut-parleur et synthèse vocale.",
        ["setup.security"] = "Sécurité",
        ["setup.security.subtitle"] = "Comment Jarvis doit-il se comporter face aux actions sensibles ?",
        ["setup.finish"] = "C'est prêt",
        ["setup.finish.subtitle"] = "Récapitulatif avant de démarrer.",
        ["setup.step"] = "Étape",
        ["setup.of"] = "sur",
        ["setup.skip"] = "Passer la configuration",
        ["setup.back"] = "Retour",
        ["setup.next"] = "Suivant",
        ["setup.save"] = "Enregistrer et terminer",
        ["setup.finish.btn"] = "Terminer",
        ["setup.saving"] = "Enregistrement…",
        ["setup.testing"] = "Test en cours…",
        ["setup.test.btn"] = "Tester ce modèle",
        ["setup.confidentiality"] = "Cette interface n'est accessible que depuis cette machine (127.0.0.1). Aucune donnée ne transite par un serveur externe pour la configuration.",
        ["setup.confidentiality.title"] = "Tout se configure ici, sur ton ordinateur uniquement.",

        // Setup - Model
        ["setup.ollama.ready"] = "Ollama est prêt",
        ["setup.ollama.offline"] = "Ollama est hors ligne",
        ["setup.ollama.retry"] = "Réessayer",
        ["setup.model.select"] = "Modèle de conversation",
        ["setup.model.none"] = "Aucun modèle détecté",
        ["setup.model.download"] = "téléchargement requis",
        ["setup.model.router"] = "Routeur actuel",
        ["setup.model.fast"] = "rapide",
        ["setup.model.reasoning"] = "raisonnement",

        // Setup - Voice
        ["setup.mic"] = "Microphone",
        ["setup.speaker"] = "Haut-parleur",
        ["setup.mic.default"] = "Par défaut (système)",
        ["setup.voice.mode"] = "Activer le mode vocal",
        ["setup.wake.word"] = "Écouter le mot-clé « Jarvis »",
        ["setup.voice.advanced"] = "Les réglages avancés de voix (voix TTS, vitesse, ducking audio…) sont dans",

        // Setup - Security
        ["setup.dev.mode"] = "Mode développeur",
        ["setup.dev.mode.desc"] = "Jarvis agit de façon autonome, sans demander confirmation à chaque action.",
        ["setup.skip.confirm"] = "Autoriser la désactivation des confirmations",
        ["setup.skip.confirm.desc"] = "Permet de passer outre les garde-fous pour les actions à risque.",
        ["setup.security.default"] = "Par défaut, Jarvis reste en mode sûr : les actions sensibles te sont soumises avant exécution.",

        // Setup - Finish
        ["setup.recap.model"] = "Modèle",
        ["setup.recap.default"] = "par défaut",
        ["setup.recap.voice"] = "Mode vocal",
        ["setup.recap.on"] = "activé",
        ["setup.recap.off"] = "désactivé",
        ["setup.recap.keyword"] = "Mot-clé",
        ["setup.recap.security"] = "Sécurité",
        ["setup.recap.dev"] = "mode développeur",
        ["setup.recap.safe"] = "mode sûr",
        ["setup.recap.finish"] = "Clique sur",
        ["setup.recap.finish2"] = "pour ouvrir Jarvis. Tu pourras relancer cet assistant à tout moment depuis",

        // Steps
        ["step.1"] = "1. Choisir le moteur d'intelligence artificielle et son modèle.",
        ["step.2"] = "2. Régler le microphone, le haut-parleur et la voix.",
        ["step.3"] = "3. Définir le niveau de sécurité des actions.",
        ["step.find.settings"] = "Tu pourras tout retrouver ensuite dans",

        // Settings page
        ["settings.title"] = "Paramètres",
        ["settings.advanced"] = "Paramètres avancés",
        ["settings.language"] = "Langue",
        ["settings.language.ui"] = "Langue de l'interface",
        ["settings.reset"] = "Réinitialiser les paramètres",
        ["settings.reset.confirm"] = "Êtes-vous sûr ? Tous les paramètres reviendront aux valeurs par défaut.",

        // Chat
        ["chat.placeholder"] = "Écris un message à Jarvis…",
        ["chat.send"] = "Envoyer",

        // Common
        ["common.save"] = "Enregistrer",
        ["common.cancel"] = "Annuler",
        ["common.delete"] = "Supprimer",
        ["common.confirm"] = "Confirmer",
        ["common.yes"] = "Oui",
        ["common.no"] = "Non",
        ["common.loading"] = "Chargement…",
        ["common.error"] = "Erreur",
        ["common.success"] = "Succès",

        // Features
        ["feature.face.recognition"] = "Reconnaissance faciale",
        ["feature.voice.cloning"] = "Clonage vocal",
        ["feature.overlay"] = "Overlay AR",
        ["feature.hologram"] = "Hologramme 3D",
        ["feature.multi.monitor"] = "Multi-écrans",
        ["feature.emotion"] = "Détection d'émotions",
        ["feature.network"] = "Sécurité réseau",
        ["feature.gaze"] = "Suivi du regard",
        ["feature.auto.improve"] = "Auto-amélioration",

        // Voice presets
        ["voice.jarvis"] = "JARVIS (Français)",
        ["voice.jarvis.en"] = "JARVIS (English)",
        ["voice.jarvis.deep"] = "JARVIS Deep",
        ["voice.jarvis.calm"] = "JARVIS Calm",
        ["voice.jarvis.energy"] = "JARVIS Energy",
    };

    // ═══════════════════════════════════════════════════════════════
    // ENGLISH STRINGS
    // ═══════════════════════════════════════════════════════════════
    private static Dictionary<string, string> GetEnglishStrings() => new()
    {
        // Setup
        ["setup.welcome"] = "Welcome",
        ["setup.welcome.subtitle"] = "Let's get to know each other in a few steps. Nothing is permanent, everything can be changed later.",
        ["setup.language"] = "Language",
        ["setup.language.subtitle"] = "Choose the interface language and Jarvis responses.",
        ["setup.language.choose"] = "Interface language",
        ["setup.model"] = "Artificial Intelligence",
        ["setup.model.subtitle"] = "Let's verify the engine and model that will respond to you.",
        ["setup.voice"] = "Voice",
        ["setup.voice.subtitle"] = "Microphone, speaker and text-to-speech.",
        ["setup.security"] = "Security",
        ["setup.security.subtitle"] = "How should Jarvis behave with sensitive actions?",
        ["setup.finish"] = "All Set",
        ["setup.finish.subtitle"] = "Summary before starting.",
        ["setup.step"] = "Step",
        ["setup.of"] = "of",
        ["setup.skip"] = "Skip setup",
        ["setup.back"] = "Back",
        ["setup.next"] = "Next",
        ["setup.save"] = "Save and finish",
        ["setup.finish.btn"] = "Finish",
        ["setup.saving"] = "Saving…",
        ["setup.testing"] = "Testing…",
        ["setup.test.btn"] = "Test this model",
        ["setup.confidentiality"] = "This interface is only accessible from this machine (127.0.0.1). No data is sent to an external server for configuration.",
        ["setup.confidentiality.title"] = "Everything is configured here, on your computer only.",

        // Setup - Model
        ["setup.ollama.ready"] = "Ollama is ready",
        ["setup.ollama.offline"] = "Ollama is offline",
        ["setup.ollama.retry"] = "Retry",
        ["setup.model.select"] = "Conversation model",
        ["setup.model.none"] = "No model detected",
        ["setup.model.download"] = "download required",
        ["setup.model.router"] = "Current router",
        ["setup.model.fast"] = "fast",
        ["setup.model.reasoning"] = "reasoning",

        // Setup - Voice
        ["setup.mic"] = "Microphone",
        ["setup.speaker"] = "Speaker",
        ["setup.mic.default"] = "Default (system)",
        ["setup.voice.mode"] = "Enable voice mode",
        ["setup.wake.word"] = "Listen for the wake word \"Jarvis\"",
        ["setup.voice.advanced"] = "Advanced voice settings (TTS voice, speed, audio ducking…) are in",

        // Setup - Security
        ["setup.dev.mode"] = "Developer mode",
        ["setup.dev.mode.desc"] = "Jarvis acts autonomously, without asking for confirmation on every action.",
        ["setup.skip.confirm"] = "Allow disabling confirmations",
        ["setup.skip.confirm.desc"] = "Allows bypassing safety guards for risky actions.",
        ["setup.security.default"] = "By default, Jarvis stays in safe mode: sensitive actions are submitted to you before execution.",

        // Setup - Finish
        ["setup.recap.model"] = "Model",
        ["setup.recap.default"] = "default",
        ["setup.recap.voice"] = "Voice mode",
        ["setup.recap.on"] = "enabled",
        ["setup.recap.off"] = "disabled",
        ["setup.recap.keyword"] = "Wake word",
        ["setup.recap.security"] = "Security",
        ["setup.recap.dev"] = "developer mode",
        ["setup.recap.safe"] = "safe mode",
        ["setup.recap.finish"] = "Click",
        ["setup.recap.finish2"] = "to open Jarvis. You can restart this assistant anytime from",

        // Steps
        ["step.1"] = "1. Choose the AI engine and its model.",
        ["step.2"] = "2. Set up the microphone, speaker and voice.",
        ["step.3"] = "3. Define the security level for actions.",
        ["step.find.settings"] = "You can find everything later in",

        // Settings page
        ["settings.title"] = "Settings",
        ["settings.advanced"] = "Advanced Settings",
        ["settings.language"] = "Language",
        ["settings.language.ui"] = "Interface language",
        ["settings.reset"] = "Reset settings",
        ["settings.reset.confirm"] = "Are you sure? All settings will return to default values.",

        // Chat
        ["chat.placeholder"] = "Type a message to Jarvis…",
        ["chat.send"] = "Send",

        // Common
        ["common.save"] = "Save",
        ["common.cancel"] = "Cancel",
        ["common.delete"] = "Delete",
        ["common.confirm"] = "Confirm",
        ["common.yes"] = "Yes",
        ["common.no"] = "No",
        ["common.loading"] = "Loading…",
        ["common.error"] = "Error",
        ["common.success"] = "Success",

        // Features
        ["feature.face.recognition"] = "Face Recognition",
        ["feature.voice.cloning"] = "Voice Cloning",
        ["feature.overlay"] = "AR Overlay",
        ["feature.hologram"] = "3D Hologram",
        ["feature.multi.monitor"] = "Multi-Monitor",
        ["feature.emotion"] = "Emotion Detection",
        ["feature.network"] = "Network Security",
        ["feature.gaze"] = "Eye Tracking",
        ["feature.auto.improve"] = "Auto-Improvement",

        // Voice presets
        ["voice.jarvis"] = "JARVIS (French)",
        ["voice.jarvis.en"] = "JARVIS (English)",
        ["voice.jarvis.deep"] = "JARVIS Deep",
        ["voice.jarvis.calm"] = "JARVIS Calm",
        ["voice.jarvis.energy"] = "JARVIS Energy",
    };
}
