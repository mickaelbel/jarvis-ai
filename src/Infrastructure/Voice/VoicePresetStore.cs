using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Voice;

/// <summary>
/// A voice preset with metadata and optional reference audio for cloning.
/// </summary>
public sealed class JarvisVoicePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Engine { get; set; } = "edge"; // edge, piper, xtts, windows
    public string VoiceId { get; set; } = ""; // Edge voice name, Piper model, etc.
    public string Language { get; set; } = "fr";
    public float Speed { get; set; } = 1.0f;
    public float Pitch { get; set; } = 0.0f; // -1.0 to 1.0
    public float Volume { get; set; } = 1.0f;
    public bool IsSystem { get; set; } // Built-in presets can't be deleted
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? ReferenceAudioPath { get; set; } // For voice cloning
}

/// <summary>
/// Manages voice presets including built-in JARVIS voices and user-created ones.
/// </summary>
public sealed class VoicePresetStore
{
    private readonly string _dir;
    private readonly string _presetsPath;
    private readonly object _lock = new();

    public VoicePresetStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "voice-presets");
        Directory.CreateDirectory(_dir);
        _presetsPath = Path.Combine(_dir, "presets.json");
        InitializeBuiltInPresets();
    }

    public IReadOnlyList<JarvisVoicePreset> LoadAll()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_presetsPath))
                {
                    var json = File.ReadAllText(_presetsPath);
                    var list = JsonSerializer.Deserialize<List<JarvisVoicePreset>>(json) ?? new();
                    return list;
                }
            }
            catch { }
            return GetBuiltInPresets();
        }
    }

    public JarvisVoicePreset? GetById(string id)
    {
        lock (_lock)
        {
            return LoadAll().FirstOrDefault(p => p.Id == id);
        }
    }

    public JarvisVoicePreset? GetByName(string name)
    {
        lock (_lock)
        {
            return LoadAll().FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Save(JarvisVoicePreset preset)
    {
        lock (_lock)
        {
            var list = LoadAll().ToList();
            var idx = list.FindIndex(p => p.Id == preset.Id);
            if (idx >= 0) list[idx] = preset;
            else list.Add(preset);
            Persist(list);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var list = LoadAll().ToList();
            var preset = list.FirstOrDefault(p => p.Id == id);
            if (preset?.IsSystem == true) return false; // Can't delete built-in

            var removed = list.RemoveAll(p => p.Id == id);
            if (removed > 0) Persist(list);
            return removed > 0;
        }
    }

    private void InitializeBuiltInPresets()
    {
        lock (_lock)
        {
            if (File.Exists(_presetsPath)) return;

            var presets = GetBuiltInPresets();
            Persist(presets);
        }
    }

    private static List<JarvisVoicePreset> GetBuiltInPresets() => new()
    {
        // ═══ JARVIS Presets (Iron Man style) ═══

        new JarvisVoicePreset
        {
            Id = "jarvis-fr",
            Name = "jarvis",
            DisplayName = "JARVIS (Francais)",
            Description = "Voix masculine, posée et professionnelle — style JARVIS d'Iron Man. Voix neurale Microsoft Edge.",
            Engine = "edge",
            VoiceId = "fr-FR-HenriNeural",
            Language = "fr",
            Speed = 0.95f,
            Pitch = 0.0f,
            Volume = 1.0f,
            IsSystem = true
        },

        new JarvisVoicePreset
        {
            Id = "jarvis-en",
            Name = "jarvis-en",
            DisplayName = "JARVIS (English)",
            Description = "British male voice — JARVIS from Iron Man. Neural voice.",
            Engine = "edge",
            VoiceId = "en-GB-RyanNeural",
            Language = "en",
            Speed = 0.95f,
            Pitch = 0.0f,
            Volume = 1.0f,
            IsSystem = true
        },

        new JarvisVoicePreset
        {
            Id = "jarvis-deep",
            Name = "jarvis-deep",
            DisplayName = "JARVIS Deep",
            Description = "Voix masculine grave et autoritaire — style JARVIS version militaire.",
            Engine = "edge",
            VoiceId = "fr-FR-LucienNeural",
            Language = "fr",
            Speed = 0.90f,
            Pitch = -0.1f,
            Volume = 1.0f,
            IsSystem = true
        },

        new JarvisVoicePreset
        {
            Id = "jarvis-calm",
            Name = "jarvis-calm",
            DisplayName = "JARVIS Calm",
            Description = "Voix masculine douce et apaisante — style JARVIS mode veille.",
            Engine = "edge",
            VoiceId = "fr-FR-HenriNeural",
            Language = "fr",
            Speed = 0.85f,
            Pitch = 0.05f,
            Volume = 0.9f,
            IsSystem = true
        },

        new JarvisVoicePreset
        {
            Id = "jarvis-energy",
            Name = "jarvis-energy",
            DisplayName = "JARVIS Energy",
            Description = "Voix masculine dynamique — style JARVIS en mode combat.",
            Engine = "edge",
            VoiceId = "fr-FR-HenriNeural",
            Language = "fr",
            Speed = 1.1f,
            Pitch = 0.0f,
            Volume = 1.0f,
            IsSystem = true
        },

        // ═══ Other System Presets ═══

        new JarvisVoicePreset
        {
            Id = "french-female",
            Name = "emily",
            DisplayName = "Emily (Neutre)",
            Description = "Voix feminine neutre, claire et professionnelle.",
            Engine = "edge",
            VoiceId = "fr-FR-DeniseNeural",
            Language = "fr",
            Speed = 1.0f,
            IsSystem = true
        },

        new JarvisVoicePreset
        {
            Id = "french-male",
            Name = "tom",
            DisplayName = "Tom (Piper)",
            Description = "Voix masculine naturelle, moteur local Piper.",
            Engine = "piper",
            VoiceId = "fr_FR-tom-medium",
            Language = "fr",
            Speed = 1.0f,
            IsSystem = true
        },

        new JarvisVoicePreset
        {
            Id = "english-male",
            Name = "ryan",
            DisplayName = "Ryan (English)",
            Description = "Voix masculine anglaise, style BBC.",
            Engine = "edge",
            VoiceId = "en-GB-RyanNeural",
            Language = "en",
            Speed = 1.0f,
            IsSystem = true
        },

        new JarvisVoicePreset
        {
            Id = "windows-sapi",
            Name = "windows",
            DisplayName = "Windows SAPI",
            Description = "Voix systeme Windows (fallback, basse qualite).",
            Engine = "windows",
            VoiceId = "",
            Language = "fr",
            Speed = 1.0f,
            IsSystem = true
        }
    };

    private void Persist(List<JarvisVoicePreset> list)
    {
        try
        {
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_presetsPath, json);
        }
        catch { }
    }
}
