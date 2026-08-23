using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Voice;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

// Reconnaissance musicale « c'est quoi cette musique ? » (docs/musique.py) :
// capture 8 s (son du PC via WASAPI loopback, ou micro), puis shazamio dans un
// venv isolé (musique/.venv-shazam). À la demande ou en veille continue.
public sealed class MusicTool : ITool
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(90);

    private readonly IntegrationsStore _store;
    private readonly Lazy<VoiceConversationService> _voice;
    private readonly ILogger<MusicTool> _logger;

    private readonly object _watchLock = new();
    private CancellationTokenSource? _watchCts;
    private string _watchStatus = "arrêté";
    private string _lastTrack = "";

    public MusicTool(IntegrationsStore store, Lazy<VoiceConversationService> voice, ILogger<MusicTool> logger)
    {
        _store = store;
        _voice = voice;
        _logger = logger;
    }

    public string Name => "musique";
    public string Description =>
        "Reconnaissance musicale Shazam locale. Actions : identifier (à la demande), historique, " +
        "watch start (veille continue du son du PC : annonce chaque morceau), watch stop, watch status.";
    public string Category => "media";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "J'écoute…";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "identifier | historique | watch", typeof(string), required: true),
        new("source", "loopback | micro (défaut = paramètre Musique.Source)", typeof(string)),
        new("mode", "start | stop | status (action watch)", typeof(string))
    };

    private static string? FindRepoDir(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            var devCandidate = Path.Combine(current.FullName, relative.Replace('/', '\\'));
            if (File.Exists(devCandidate)) return devCandidate;
            current = current.Parent;
        }
        return null;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        try
        {
            var resultTask = (action?.ToLowerInvariant()) switch
            {
                "identifier" => IdentifierAsync(parameters, ct),
                "historique" => Task.FromResult(Historique()),
                "watch" => Task.FromResult(Watch(parameters)),
                _ => Task.FromResult(ToolResult.Failed($"Action inconnue : {action}. Valides : identifier, historique, watch"))
            };
            return await resultTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Musique] {Action} échoué", action);
            return ToolResult.Failed($"Erreur musique : {ex.Message}");
        }
    }

    private async Task<ToolResult> IdentifierAsync(IReadOnlyDictionary<string, string> p, CancellationToken ct)
    {
        var cfg = FindRepoDir("musique/reconnaisseur.py");
        if (cfg is null)
            return ToolResult.Failed("Reconnaisseur introuvable : lance scripts/setup_musique.py une fois (py -3.12 scripts/setup_musique.py).");

        var settings = _store.Get().Musique;
        var source = p.GetValueOrDefault("source") ?? settings.Source;
        var secondes = Math.Clamp(settings.Secondes is > 0 ? settings.Secondes : 8, 4, 15);

        var capturesDir = Path.Combine(Path.GetDirectoryName(cfg)!, "captures");
        Directory.CreateDirectory(capturesDir);
        var wavPath = Path.Combine(capturesDir, $"capture_{DateTime.Now:yyyyMMdd-HHmmss}.wav");

        // Bip d'annonce façon jarvis14 : console beep 880 Hz avant capture
        try { Console.Beep(880, 150); } catch { }

        if (source == "micro")
            await CaptureMicAsync(wavPath, secondes, ct);
        else
            await CaptureLoopbackAsync(wavPath, secondes, ct);

        var info = await RecognizeFileAsync(cfg, wavPath, ct);
        if (info is null)
            return ToolResult.Failed("Pas reconnu. Approche-toi des enceintes ou monte le volume.");

        AppendHistorique($"{DateTime.Now:dd/MM/yyyy HH:mm} — {info.Titre} — {info.Artiste}" + (string.IsNullOrEmpty(info.Album) ? "" : $" ({info.Album}{(string.IsNullOrEmpty(info.Annee) ? "" : $", {info.Annee}")})"));
        _logger.LogInformation("[Musique] Reconnu : {Titre} — {Artiste}", info.Titre, info.Artiste);
        return ToolResult.Succeeded($"C'est « {info.Titre} » de {info.Artiste}." + (string.IsNullOrEmpty(info.Album) ? "" : $" Album : {info.Album} ({info.Annee})."));
    }

    private sealed record MusicInfo(string Titre, string Artiste, string Album, string Annee);

    /// <summary>Lance shazamio sur un wav ; null si non reconnu / erreur.</summary>
    private async Task<MusicInfo?> RecognizeFileAsync(string cfgPath, string wavPath, CancellationToken ct)
    {
        var venvPython = Path.Combine(Path.GetDirectoryName(cfgPath)!, ".venv-shazam", "Scripts", "python.exe");
        if (!File.Exists(venvPython))
        {
            _logger.LogWarning("[Musique] venv shazam absent");
            return null;
        }

        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = venvPython,
            Arguments = $"\"{cfgPath}\" \"{wavPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var jsonStart = stdout.LastIndexOf('{');
        if (jsonStart < 0) return null;
        using var doc = JsonDocument.Parse(stdout[jsonStart..]);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;

        return new MusicInfo(
            root.GetProperty("titre").GetString() ?? "",
            root.GetProperty("artiste").GetString() ?? "",
            root.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "",
            root.TryGetProperty("annee", out var an) ? an.GetString() ?? "" : "");
    }

    // ── Veille continue : échantillonne le son du PC toutes les 90 s ─────────
    private ToolResult Watch(IReadOnlyDictionary<string, string> p)
    {
        var mode = (p.GetValueOrDefault("mode") ?? "start").ToLowerInvariant();
        lock (_watchLock)
        {
            if (mode == "stop")
            {
                _watchCts?.Cancel();
                _watchStatus = "arrêté";
                return ToolResult.Succeeded("Veille musicale arrêtée.");
            }
            if (mode == "status")
                return ToolResult.Succeeded($"Veille musicale : {_watchStatus}");
            if (_watchCts is not null && !_watchCts.IsCancellationRequested)
                return ToolResult.Succeeded("La veille tourne déjà.");

            _watchCts = new CancellationTokenSource();
            _watchStatus = "démarrage";
            _lastTrack = "";
            _ = Task.Run(() => WatchLoop(_watchCts.Token), _watchCts.Token);
            return ToolResult.Succeeded("Veille musicale lancée — je t'annonce chaque nouveau morceau joué sur le PC (~toutes les 90 s).");
        }
    }

    private async Task WatchLoop(CancellationToken ct)
    {
        var cfg = FindRepoDir("musique/reconnaisseur.py");
        if (cfg is null || !File.Exists(Path.Combine(Path.GetDirectoryName(cfg)!, ".venv-shazam", "Scripts", "python.exe")))
        {
            lock (_watchLock) _watchStatus = "erreur (setup manquant)";
            return;
        }

        var capturesDir = Path.Combine(Path.GetDirectoryName(cfg)!, "captures");
        Directory.CreateDirectory(capturesDir);
        lock (_watchLock) _watchStatus = "actif";

        while (!ct.IsCancellationRequested)
        {
            var wavPath = Path.Combine(capturesDir, $"watch_{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            try
            {
                await CaptureLoopbackAsync(wavPath, 10, ct);
                var info = await RecognizeFileAsync(cfg, wavPath, ct);
                if (info is not null)
                {
                    var cle = $"{info.Titre}|{info.Artiste}";
                    bool nouveau;
                    lock (_watchLock)
                    {
                        nouveau = cle != _lastTrack;
                        _lastTrack = cle;
                    }
                    AppendHistorique($"{DateTime.Now:dd/MM/yyyy HH:mm} — {info.Titre} — {info.Artiste}");
                    if (nouveau)
                    {
                        _logger.LogInformation("[Musique][veille] {Titre} — {Artiste}", info.Titre, info.Artiste);
                        await _voice.Value.SpeakAsync($"Ça c'est {info.Titre} de {info.Artiste}.", ct);
                    }
                    lock (_watchLock) _watchStatus = "actif";
                }
                else
                {
                    lock (_watchLock) _watchStatus = "actif (rien reconnu au dernier passage)";
                }
                try { File.Delete(wavPath); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Musique] veille : passage échoué");
                try { File.Delete(wavPath); } catch { }
            }

            try { await Task.Delay(WatchInterval, ct); }
            catch (OperationCanceledException) { break; }
        }

        lock (_watchLock) _watchStatus = "arrêté";
    }

    private static async Task CaptureLoopbackAsync(string wavPath, int secondes, CancellationToken ct)
    {
        using var capture = new WasapiLoopbackCapture();
        using var writer = new WaveFileWriter(wavPath, capture.WaveFormat);
        var tcs = new TaskCompletionSource();
        capture.RecordingStopped += (_, _) => tcs.TrySetResult();
        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
        capture.StartRecording();
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(secondes), ct);
        }
        finally
        {
            capture.StopRecording();
        }
    }

    private static async Task CaptureMicAsync(string wavPath, int secondes, CancellationToken ct)
    {
        using var capture = new WaveInEvent { WaveFormat = new WaveFormat(44100, 16, 2) };
        using var writer = new WaveFileWriter(wavPath, capture.WaveFormat);
        var tcs = new TaskCompletionSource();
        capture.RecordingStopped += (_, _) => tcs.TrySetResult();
        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
        capture.StartRecording();
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(secondes), ct);
        }
        finally
        {
            capture.StopRecording();
        }
    }

    private static string HistoriquePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "notes");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "musiques.md");
    }

    private static ToolResult Historique()
    {
        var path = HistoriquePath();
        if (!File.Exists(path)) return ToolResult.Succeeded("Aucune musique identifiée pour l'instant.");
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(10);
        return ToolResult.Succeeded("MUSIQUES IDENTIFIÉES :\n" + string.Join("\n", lines.Select(l => $"  • {l}")));
    }

    private static void AppendHistorique(string ligne)
    {
        File.AppendAllText(HistoriquePath(), ligne + "\n");
    }
}
