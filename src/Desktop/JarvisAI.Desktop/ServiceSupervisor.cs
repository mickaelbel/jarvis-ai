using System.Diagnostics;
using System.IO;
using System.Net.Http;
using JarvisAI.Infrastructure.Voice;

namespace JarvisAI.Desktop;

// Superviseur des services externes : Ollama, serveur STT (faster-whisper) et
// serveur wake-word (openwakeword). Pré-chauffe le modèle Whisper au boot et
// surveille les serveurs voix : s'ils crashent en pleine session, ils sont
// relancés automatiquement (watchdog 30 s).
public sealed class ServiceSupervisor : IAsyncDisposable
{
    private readonly JarvisAI.Infrastructure.AI.OllamaLauncher _ollama;
    private readonly List<Process> _processes = new();
    private readonly object _lock = new();
    private System.Threading.Timer? _watchdog;
    private readonly Dictionary<string, DateTime> _lastLaunch = new();
    // Scripts définitivement indisponibles (python/script absent sur le disque) :
    // inutile de les relancer à chaque passage du watchdog, ça ne ferait que
    // tourner en boucle sans jamais aboutir (et spammer le log).
    private readonly HashSet<string> _unavailable = new();
    private static readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(3) };

    public ServiceSupervisor(JarvisAI.Infrastructure.AI.OllamaLauncher ollama)
    {
        _ollama = ollama;
    }

    public async Task StartAsync()
    {
        await EnsureOllamaAsync();

        // Démarrage en parallèle des serveurs voix (indépendants les uns des autres)
        // au lieu de séquentiel : économise ~10-20 s au démarrage.
        var voiceTasks = new[]
        {
            StartVoiceServerAsync("stt_server.py", VoicePaths.SttPort),
            StartVoiceServerAsync("wakeword_server.py", VoicePaths.WakeWordPort),
            StartVoiceServerAsync("edge_tts_server.py", VoicePaths.EdgeTtsPort)
        };
        await Task.WhenAll(voiceTasks);

        WarmupStt();

        // Watchdog : vérifie périodiquement que les serveurs voix répondent.
        _watchdog = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (!await IsReachableAsync(_probe, VoicePaths.SttHealth) && !IsUnavailable("stt_server.py"))
                {
                    App.Log("[Supervisor] Watchdog : STT injoignable, relance");
                    await StartVoiceServerAsync("stt_server.py", VoicePaths.SttPort);
                }
                if (!await IsReachableAsync(_probe, VoicePaths.WakeWordHealth) && !IsUnavailable("wakeword_server.py"))
                {
                    App.Log("[Supervisor] Watchdog : wake-word injoignable, relance");
                    await StartVoiceServerAsync("wakeword_server.py", VoicePaths.WakeWordPort);
                }
                if (!await IsReachableAsync(_probe, VoicePaths.EdgeTtsHealth) && !IsUnavailable("edge_tts_server.py"))
                {
                    App.Log("[Supervisor] Watchdog : edge-tts injoignable, relance");
                    await StartVoiceServerAsync("edge_tts_server.py", VoicePaths.EdgeTtsPort);
                }
            }
            catch (Exception ex)
            {
                App.Log("[Supervisor] Watchdog error: " + ex.Message);
            }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public ValueTask DisposeAsync()
    {
        _watchdog?.Dispose();
        Stop();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public void Stop()
    {
        lock (_lock)
        {
            foreach (var p in _processes)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                try { p.Dispose(); } catch { }
            }
            _processes.Clear();
        }
    }

    private async Task EnsureOllamaAsync()
    {
        try
        {
            await _ollama.EnsureRunningAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            App.Log("[Supervisor] Ollama non démarré : " + ex.Message);
        }
    }

    private bool IsUnavailable(string scriptName)
    {
        lock (_lock) return _unavailable.Contains(scriptName);
    }

    private async Task StartVoiceServerAsync(string scriptName, int port)
    {
        try
        {
            if (await IsReachableAsync(_probe, $"http://127.0.0.1:{port}/health"))
                return;

            // Anti-doublon : un serveur Python (torch, faster-whisper) peut
            // prendre 30-60 s à démarrer. On ne relance pas avant 90 s.
            lock (_lock)
            {
                if (_unavailable.Contains(scriptName)) return;
                if (_lastLaunch.TryGetValue(scriptName, out var previous) &&
                    (DateTime.UtcNow - previous).TotalSeconds < 90)
                    return;
                _lastLaunch[scriptName] = DateTime.UtcNow;
            }

            var voiceDir = JarvisAI.Infrastructure.Voice.VoicePaths.FindVoiceDirectory();
            if (voiceDir is null)
            {
                App.Log($"[Supervisor] {scriptName} ignoré : dossier voix introuvable (désactivé)");
                lock (_lock) _unavailable.Add(scriptName);
                return;
            }

            var python = Path.Combine(voiceDir, ".venv", "Scripts", "python.exe");
            var script = Path.Combine(voiceDir, scriptName);
            if (!File.Exists(python) || !File.Exists(script))
            {
                App.Log($"[Supervisor] {scriptName} ignoré : python/script absent dans {voiceDir} (relance désactivée)");
                lock (_lock) _unavailable.Add(scriptName);
                return;
            }

            var p = Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{script}\"",
                WorkingDirectory = voiceDir,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is not null)
            {
                lock (_lock) _processes.Add(p);
                App.Log($"[Supervisor] {scriptName} démarré (PID {p.Id})");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Supervisor] Démarrage {scriptName} échoué : {ex.Message}");
        }
    }

    private void WarmupStt()
    {
        // Fire-and-forget : le modèle se charge pendant que Jarvis démarre,
        // la première commande vocale est immédiate.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(1500);
                if (await IsReachableAsync(_probe, VoicePaths.SttHealth))
                {
                    try
                    {
                        await _probe.GetAsync(VoicePaths.SttBase + "/warmup");
                        App.Log("[Supervisor] Whisper préchargé (warmup)");
                    }
                    catch { /* le serveur chargera à la première requête */ }
                    return;
                }
            }
            App.Log("[Supervisor] Warmup STT abandonné (serveur jamais prêt)");
        });
    }

    private static async Task<bool> IsReachableAsync(HttpClient client, string url)
    {
        try
        {
            var res = await client.GetAsync(url);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}