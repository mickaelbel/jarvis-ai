using JarvisAI.Application.Services;
using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace JarvisAI.Infrastructure.Audio;

/// <summary>
/// Audio ducking via NAudio.CoreAudioApi : baisse le volume de toutes les
/// sessions audio sauf Jarvis pendant la conversation vocale. La musique
/// (Spotify, VLC…) reçoit un volume cible plus bas que les autres apps.
/// Fondu enchaîné configurable.
/// </summary>
public sealed class AudioDuckingService : IAudioDuckingService
{
    private readonly ILogger<AudioDuckingService> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<uint, float> _savedVolumes = new();
    private CancellationTokenSource? _fadeCts;
    private bool _isDucking;
    private bool _disposed;

    private static readonly HashSet<string> MusicApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotify", "vlc", "foobar2000", "musicbee", "aimp",
        "itunes", "apple music", "youtube music", "tidal",
        "deezer", "amazon music", "winamp", "musicanet",
        "dopamine", "musicbriz", "audacious", "rhythmbox",
        "clementine", "strawberry", "qmmp", "gamma", "navidrome",
        "lidarr", "plex", "kodi"
    };

    public bool IsDucking
    {
        get { lock (_lock) return _isDucking; }
    }

    public AudioDuckingService(ILogger<AudioDuckingService> logger)
    {
        _logger = logger;
    }

    public async Task DuckAsync(VoiceSettings settings)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_isDucking) return;
            _isDucking = true;
        }

        try
        {
            CancelPendingFade();
            _fadeCts = new CancellationTokenSource();
            var ct = _fadeCts.Token;

            var excluded = ParseExcludedApps(settings.AudioDuckingExcludedApps);
            var selfPid = (uint)Environment.ProcessId;
            var fadeMs = Math.Max(100, settings.AudioDuckingFadeMs);

            var sessions = GetSessionEntries();
            if (sessions.Count == 0)
            {
                _logger.LogDebug("[Ducking] Aucune session audio trouvée");
                return;
            }

            _logger.LogInformation("[Ducking] Baisse du volume ({Count} sessions, fade {Fade}ms)", sessions.Count, fadeMs);

            var targets = new List<(SimpleAudioVolume volume, float target)>();
            foreach (var entry in sessions)
            {
                try
                {
                    if (entry.Pid == selfPid) continue;
                    if (excluded.Contains(entry.ProcessName)) continue;

                    var original = entry.Volume.Volume;
                    if (original <= 0.001f) continue;

                    var target = IsMusicApp(entry.ProcessName)
                        ? settings.AudioDuckingMusicVolume
                        : settings.AudioDuckingSystemVolume;

                    _savedVolumes[entry.Pid] = original;
                    targets.Add((entry.Volume, target));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Ducking] Impossible de lire le volume pour PID {Pid}", entry.Pid);
                }
            }

            if (targets.Count == 0) return;

            await FadeVolume(targets, fadeMs, ct);
            _logger.LogInformation("[Ducking] Volume baissé ({Count} sessions)", targets.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[Ducking] Fade annulée");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ducking] Erreur lors de la baisse de volume");
            lock (_lock) _isDucking = false;
        }
    }

    public async Task RestoreAsync()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (!_isDucking) return;
        }

        try
        {
            CancelPendingFade();
            _fadeCts = new CancellationTokenSource();
            var ct = _fadeCts.Token;

            var sessions = GetSessionEntries();

            var targets = new List<(SimpleAudioVolume volume, float target)>();
            foreach (var entry in sessions)
            {
                if (_savedVolumes.TryGetValue(entry.Pid, out var original))
                {
                    targets.Add((entry.Volume, original));
                }
            }

            if (targets.Count == 0)
            {
                lock (_lock) _isDucking = false;
                return;
            }

            _logger.LogInformation("[Ducking] Restauration du volume ({Count} sessions)", targets.Count);
            await FadeVolume(targets, 1500, ct);

            lock (_lock)
            {
                _savedVolumes.Clear();
                _isDucking = false;
            }
            _logger.LogInformation("[Ducking] Volume restauré");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[Ducking] Fade de restauration annulée");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ducking] Erreur lors de la restauration");
            lock (_lock)
            {
                _savedVolumes.Clear();
                _isDucking = false;
            }
        }
    }

    public async Task ToggleAsync(VoiceSettings settings)
    {
        if (IsDucking)
            await RestoreAsync();
        else
            await DuckAsync(settings);
    }

    private async Task FadeVolume(
        List<(SimpleAudioVolume volume, float target)> targets,
        int fadeMs,
        CancellationToken ct)
    {
        const int steps = 50;
        var stepMs = Math.Max(10, fadeMs / steps);

        for (int i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var progress = (float)i / steps;

            foreach (var (volume, target) in targets)
            {
                try
                {
                    var current = volume.Volume;
                    var newVolume = current + (target - current) * progress;
                    volume.Volume = Math.Clamp(newVolume, 0f, 1f);
                }
                catch { }
            }

            await Task.Delay(stepMs, ct);
        }

        foreach (var (volume, target) in targets)
        {
            try { volume.Volume = target; } catch { }
        }
    }

    private void CancelPendingFade()
    {
        try { _fadeCts?.Cancel(); } catch { }
        try { _fadeCts?.Dispose(); } catch { }
        _fadeCts = null;
    }

    private static HashSet<string> ParseExcludedApps(string excluded)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(excluded)) return set;
        foreach (var app in excluded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = app.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            set.Add(name);
        }
        return set;
    }

    private static bool IsMusicApp(string processName)
    {
        var name = processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        return MusicApps.Contains(name);
    }

    private List<(uint Pid, string ProcessName, SimpleAudioVolume Volume)> GetSessionEntries()
    {
        var result = new List<(uint, string, SimpleAudioVolume)>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device.AudioSessionManager;

            for (int i = 0; i < sessionManager.Sessions.Count; i++)
            {
                try
                {
                    AudioSessionControl session = sessionManager.Sessions[i];
                    if (session is null) continue;
                    if (session.IsSystemSoundsSession) continue;

                    SimpleAudioVolume? simpleVolume = session.SimpleAudioVolume;
                    if (simpleVolume is null) continue;

                    uint pid = session.GetProcessID;
                    string processName = session.DisplayName ?? "";

                    if (pid > 0)
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                            processName = proc.ProcessName;
                        }
                        catch { }
                    }

                    result.Add((pid, processName, simpleVolume));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Ducking] Impossible d'énumérer les sessions audio");
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingFade();

        if (_isDucking)
        {
            try
            {
                var sessions = GetSessionEntries();
                foreach (var entry in sessions)
                {
                    if (_savedVolumes.TryGetValue(entry.Pid, out var original))
                    {
                        try { entry.Volume.Volume = original; } catch { }
                    }
                }
            }
            catch { }
            lock (_lock)
            {
                _savedVolumes.Clear();
                _isDucking = false;
            }
        }
    }
}
