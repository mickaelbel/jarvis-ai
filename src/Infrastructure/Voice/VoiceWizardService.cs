using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceWizardService
{
    Task<VizardWizardResult> RunFullSetupAsync(IProgress<WizardProgress>? progress = null, CancellationToken ct = default);
    Task<WizardStepResult> TestMicrophoneAsync(CancellationToken ct = default);
    Task<WizardStepResult> TestSpeakerAsync(CancellationToken ct = default);
    Task<WizardStepResult> TestVoiceSynthesisAsync(string? voice = null, CancellationToken ct = default);
    Task<WizardStepResult> TestSpeechRecognitionAsync(CancellationToken ct = default);
    Task<WizardStepResult> TestWakeWordAsync(CancellationToken ct = default);
    Task<List<VoiceOption>> GetAvailableVoicesAsync(CancellationToken ct = default);
    VoiceConfig GetCurrentConfig();
    void SaveConfig(VoiceConfig config);
}

public sealed class VoiceWizardService : IVoiceWizardService
{
    private readonly ILogger<VoiceWizardService> _logger;
    private readonly IVoiceSetupService _setupService;
    private readonly string _configPath;
    private VoiceConfig _config;

    public VoiceWizardService(ILogger<VoiceWizardService> logger, IVoiceSetupService setupService)
    {
        _logger = logger;
        _setupService = setupService;
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "config", "voice.json");
        _config = LoadConfig();
    }

    public async Task<VizardWizardResult> RunFullSetupAsync(IProgress<WizardProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new VizardWizardResult();
        var steps = new[]
        {
            ("Vérification de Python", new Func<CancellationToken, Task<WizardStepResult>>(async ct2 =>
            {
                progress?.Report(new WizardProgress { Step = 1, TotalSteps = 7, Message = "Vérification de Python..." });
                var report = await _setupService.CheckAndSetupAsync(ct2);
                return new WizardStepResult
                {
                    Success = report.PythonFound,
                    Message = report.PythonFound ? $"Python trouvé: {report.PythonPath}" : "Python non trouvé",
                    Details = report.ErrorMessage
                };
            })),

            ("Installation des dépendances", new Func<CancellationToken, Task<WizardStepResult>>(async ct2 =>
            {
                progress?.Report(new WizardProgress { Step = 2, TotalSteps = 7, Message = "Installation des dépendances Python..." });
                var success = await _setupService.EnsurePythonDependenciesAsync(ct2);
                return new WizardStepResult { Success = success, Message = success ? "Dépendances installées" : "Erreur d'installation" };
            })),

            ("Vérification GPU/CUDA", new Func<CancellationToken, Task<WizardStepResult>>(async ct2 =>
            {
                progress?.Report(new WizardProgress { Step = 3, TotalSteps = 7, Message = "Détection GPU..." });
                var cuda = await _setupService.CheckCudaAsync(ct2);
                _config.UseCuda = cuda.TorchCuda;
                return new WizardStepResult
                {
                    Success = true,
                    Message = cuda.Available ? $"GPU: {cuda.GpuName}" : "CPU mode (pas de GPU)"
                };
            })),

            ("Test du microphone", new Func<CancellationToken, Task<WizardStepResult>>(async ct2 =>
            {
                progress?.Report(new WizardProgress { Step = 4, TotalSteps = 7, Message = "Test du microphone..." });
                return await TestMicrophoneAsync(ct2);
            })),

            ("Test des haut-parleurs", new Func<CancellationToken, Task<WizardStepResult>>(async ct2 =>
            {
                progress?.Report(new WizardProgress { Step = 5, TotalSteps = 7, Message = "Test des haut-parleurs..." });
                return await TestSpeakerAsync(ct2);
            })),

            ("Test de synthèse vocale", new Func<CancellationToken, Task<WizardStepResult>>(async ct2 =>
            {
                progress?.Report(new WizardProgress { Step = 6, TotalSteps = 7, Message = "Test de la voix..." });
                return await TestVoiceSynthesisAsync(null, ct2);
            })),

            ("Test de reconnaissance vocale", new Func<CancellationToken, Task<WizardStepResult>>(async ct2 =>
            {
                progress?.Report(new WizardProgress { Step = 7, TotalSteps = 7, Message = "Test de reconnaissance..." });
                return await TestSpeechRecognitionAsync(ct2);
            })),
        };

        foreach (var (name, test) in steps)
        {
            var stepResult = await test(ct);
            result.Steps.Add(new WizardStep { Name = name, Result = stepResult });

            if (!stepResult.Success)
            {
                result.PartialFailure = true;
                result.FailureMessages.Add($"{name}: {stepResult.Message}");
            }
        }

        result.Success = result.Steps.All(s => s.Result.Success);
        result.Config = _config;

        _logger.LogInformation("[VoiceWizard] Setup terminé: {Success}, Partial: {Partial}",
            result.Success, result.PartialFailure);

        return result;
    }

    public async Task<WizardStepResult> TestMicrophoneAsync(CancellationToken ct = default)
    {
        try
        {
            // Try to access microphone via NAudio
            var result = await Task.Run(() =>
            {
                try
                {
                    var waveIn = new NAudio.Wave.WaveInEvent();
                    var deviceCount = NAudio.Wave.WaveInEvent.DeviceCount;
                    waveIn.Dispose();

                    return new WizardStepResult
                    {
                        Success = deviceCount > 0,
                        Message = deviceCount > 0 ? $"{deviceCount} micro(s) détecté(s)" : "Aucun micro détecté"
                    };
                }
                catch (Exception ex)
                {
                    return new WizardStepResult
                    {
                        Success = false,
                        Message = "Erreur d'accès au micro",
                        Details = ex.Message
                    };
                }
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            return new WizardStepResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<WizardStepResult> TestSpeakerAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    // Just check if we can create a WaveOutEvent without error
                    var waveOut = new NAudio.Wave.WaveOutEvent();
                    waveOut.Dispose();

                    return new WizardStepResult
                    {
                        Success = true,
                        Message = "Haut-parleurs disponibles"
                    };
                }
                catch (Exception ex)
                {
                    return new WizardStepResult
                    {
                        Success = false,
                        Message = "Erreur d'accès aux haut-parleurs",
                        Details = ex.Message
                    };
                }
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            return new WizardStepResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<WizardStepResult> TestVoiceSynthesisAsync(string? voice = null, CancellationToken ct = default)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var response = await client.PostAsync("http://127.0.0.1:17004/synthesize",
                new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        text = "Bonjour, je suis Jarvis. Configuration terminée avec succès.",
                        voice = voice ?? "fr-FR-HenriNeural",
                        rate = "+0%",
                        pitch = "+0Hz"
                    }),
                    System.Text.Encoding.UTF8,
                    "application/json"),
                ct);

            if (response.IsSuccessStatusCode)
            {
                _config.SelectedVoice = voice ?? "fr-FR-HenriNeural";
                return new WizardStepResult
                {
                    Success = true,
                    Message = $"Voix testée: {_config.SelectedVoice}"
                };
            }

            return new WizardStepResult
            {
                Success = false,
                Message = "Erreur de synthèse vocale"
            };
        }
        catch (Exception ex)
        {
            return new WizardStepResult
            {
                Success = false,
                Message = "Serveur TTS non disponible",
                Details = ex.Message
            };
        }
    }

    public async Task<WizardStepResult> TestSpeechRecognitionAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var response = await client.GetAsync("http://127.0.0.1:17001/health", ct);

            if (response.IsSuccessStatusCode)
            {
                return new WizardStepResult
                {
                    Success = true,
                    Message = "Reconnaissance vocale prête"
                };
            }

            return new WizardStepResult
            {
                Success = false,
                Message = "Serveur STT non disponible"
            };
        }
        catch
        {
            return new WizardStepResult
            {
                Success = false,
                Message = "Serveur STT non démarré"
            };
        }
    }

    public async Task<WizardStepResult> TestWakeWordAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var response = await client.GetAsync("http://127.0.0.1:17002/health", ct);

            if (response.IsSuccessStatusCode)
            {
                return new WizardStepResult
                {
                    Success = true,
                    Message = "Détection de mot-clé prête"
                };
            }

            return new WizardStepResult
            {
                Success = false,
                Message = "Serveur wake-word non disponible"
            };
        }
        catch
        {
            return new WizardStepResult
            {
                Success = false,
                Message = "Serveur wake-word non démarré"
            };
        }
    }

    public async Task<List<VoiceOption>> GetAvailableVoicesAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var response = await client.GetAsync("http://127.0.0.1:17004/voices", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<List<VoiceOption>>(json) ?? new();
            }
        }
        catch { }

        // Return default French voices
        return new List<VoiceOption>
        {
            new() { Name = "fr-FR-HenriNeural", Gender = "Male", Locale = "fr-FR" },
            new() { Name = "fr-FR-DeniseNeural", Gender = "Female", Locale = "fr-FR" },
            new() { Name = "fr-FR-EloiseNeural", Gender = "Female", Locale = "fr-FR" },
        };
    }

    public VoiceConfig GetCurrentConfig() => _config;

    public void SaveConfig(VoiceConfig config)
    {
        _config = config;
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch { }
    }

    private VoiceConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<VoiceConfig>(json) ?? new VoiceConfig();
            }
        }
        catch { }

        return new VoiceConfig
        {
            SelectedVoice = "fr-FR-HenriNeural",
            WakeWord = "jarvis",
            WakeWordSensitivity = 0.5f,
            PushToTalkKey = "Ctrl+Alt+J",
            UseCuda = true,
            SttModel = "small",
            Volume = 0.8f,
            Speed = 1.0f,
            AutoStartVoice = true,
            ShowSubtitles = true
        };
    }
}

public sealed class VizardWizardResult
{
    public bool Success { get; set; }
    public bool PartialFailure { get; set; }
    public List<WizardStep> Steps { get; set; } = new();
    public List<string> FailureMessages { get; set; } = new();
    public VoiceConfig? Config { get; set; }
}

public sealed class WizardStep
{
    public string Name { get; set; } = "";
    public WizardStepResult Result { get; set; } = new();
}

public sealed class WizardStepResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? Details { get; set; }
}

public sealed class WizardProgress
{
    public int Step { get; set; }
    public int TotalSteps { get; set; }
    public string Message { get; set; } = "";
    public double Percentage => (double)Step / TotalSteps * 100;
}

public sealed class VoiceOption
{
    public string Name { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Locale { get; set; } = "";
}

public sealed class VoiceConfig
{
    public string SelectedVoice { get; set; } = "fr-FR-HenriNeural";
    public string WakeWord { get; set; } = "jarvis";
    public float WakeWordSensitivity { get; set; } = 0.5f;
    public string PushToTalkKey { get; set; } = "Ctrl+Alt+J";
    public bool UseCuda { get; set; } = true;
    public string SttModel { get; set; } = "small";
    public float Volume { get; set; } = 0.8f;
    public float Speed { get; set; } = 1.0f;
    public bool AutoStartVoice { get; set; } = true;
    public bool ShowSubtitles { get; set; } = true;
    public bool EnableBargeIn { get; set; } = true;
    public bool EnableNoiseReduction { get; set; } = true;
    public int SilenceTimeoutMs { get; set; } = 2000;
    public string Language { get; set; } = "fr";
}
