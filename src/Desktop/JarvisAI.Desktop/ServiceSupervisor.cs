using System.Diagnostics;
using System.IO;
using System.Net.Http;

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
    private static readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(3) };

    public ServiceSupervisor(JarvisAI.Infrastructure.AI.OllamaLauncher ollama)
    {
        _ollama = ollama;
    }

    public async Task StartAsync()
    {
        await EnsureOllamaAsync();
        await StartVoiceServerAsync("stt_server.py", 17001);
        await StartVoiceServerAsync("wakeword_server.py", 17002);
        WarmupStt();

        // Watchdog : vérifie périodiquement que les serveurs voix répondent.
        _watchdog = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (!await IsReachableAsync(_probe, "http://127.0.0.1:17001/health"))
                {
                    App.Log("[Supervisor] Watchdog : STT injoignable, relance");
                    await StartVoiceServerAsync("stt_server.py", 17001);
                }
                if (!await IsReachableAsync(_probe, "http://127.0.0.1:17002/health"))
                {
                    App.Log("[Supervisor] Watchdog : wake-word injoignable, relance");
                    await StartVoiceServerAsync("wakeword_server.py", 17002);
                }
            }
            catch (Exception ex)
            {
                App.Log("[Supervisor] Watchdog error: " + ex.Message);
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync()
    {
        _watchdog?.Dispose();
        Stop();
        GC.SuppressFinalize(this);
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

    private async Task StartVoiceServerAsync(string scriptName, int port)
    {
        try
        {
            if (await IsReachableAsync(_probe, $"http://127.0.0.1:{port}/health"))
                return;

            var voiceDir = JarvisAI.Infrastructure.Voice.VoicePaths.FindVoiceDirectory();
            if (voiceDir is null)
            {
                App.Log($"[Supervisor] {scriptName} ignoré : dossier voix introuvable");
                return;
            }

            var python = Path.Combine(voiceDir, ".venv", "Scripts", "python.exe");
            var script = Path.Combine(voiceDir, scriptName);
            if (!File.Exists(python) || !File.Exists(script))
            {
                App.Log($"[Supervisor] {scriptName} ignoré : python/script absent dans {voiceDir}");
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
                if (await IsReachableAsync(_probe, "http://127.0.0.1:17001/health"))
                {
                    try
                    {
                        await _probe.GetAsync("http://127.0.0.1:17001/warmup");
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