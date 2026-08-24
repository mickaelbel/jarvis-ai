namespace JarvisAI.Web.Services;

/// <summary>
/// État observable du moteur vocal Desktop (singleton DI). Mis à jour par
/// BackgroundVoiceEngine / VoiceHostedService côté process, exposé à l'UI
/// via /api/app-voice/status et poussé en temps réel par VoiceNotifier
/// (mise à jour périodique sur SignalR).
/// </summary>
public sealed class AppVoiceStatus
{
    /// <summary>Niveau micro instantané (RMS 0-1) pour la waveform du HUD.</summary>
    public double LastRms { get; set; }

    private readonly object _lock = new();
    private readonly List<VoiceTranscriptItem> _transcripts = new();
    private const int MaxTranscripts = 100;
    private bool _engineActive;
    private string _engineState = "idle";
    private string _engineMessage = "";
    private string _captureMode = "inconnu";
    private string _microState = "inconnu";
    private string _wakeWordState = "inconnu";
    private string _sttState = "inconnu";
    private string _ttsState = "inconnu";
    private string _listeningState = "inconnu";
    private string _ambient = "";
    private DateTime? _startedAt;

    public bool EngineActive
    {
        get { lock (_lock) return _engineActive; }
        set { lock (_lock) _engineActive = value; }
    }

    public string EngineState
    {
        get { lock (_lock) return _engineState; }
        set { lock (_lock) _engineState = value; }
    }

    public string EngineMessage
    {
        get { lock (_lock) return _engineMessage; }
        set { lock (_lock) _engineMessage = value; }
    }

    /// <summary>"WASAPI", "WaveIn" ou "inconnu" : voie de capture active.</summary>
    public string CaptureMode
    {
        get { lock (_lock) return _captureMode; }
        set { lock (_lock) _captureMode = value; }
    }

    public string MicroState
    {
        get { lock (_lock) return _microState; }
        set { lock (_lock) _microState = value; }
    }

    public string WakeWordState
    {
        get { lock (_lock) return _wakeWordState; }
        set { lock (_lock) _wakeWordState = value; }
    }

    public string SttState
    {
        get { lock (_lock) return _sttState; }
        set { lock (_lock) _sttState = value; }
    }

    public string TtsState
    {
        get { lock (_lock) return _ttsState; }
        set { lock (_lock) _ttsState = value; }
    }

    public string ListeningState
    {
        get { lock (_lock) return _listeningState; }
        set { lock (_lock) _listeningState = value; }
    }

    /// <summary>Résumé du contexte sonore environnant (fenêtre glissante).</summary>
    public string Ambient
    {
        get { lock (_lock) return _ambient; }
        set { lock (_lock) _ambient = value; }
    }

    public DateTime? StartedAt
    {
        get { lock (_lock) return _startedAt; }
        set { lock (_lock) _startedAt = value; }
    }

    /// <summary>Ajoute une transcription captée par le moteur vocal (feed du
    /// panneau de debug : ce que le système entend en temps réel). Les partiels
    /// consécutifs fusionnent en une seule entrée qui évolue (effet défilement).</summary>
    public void AddTranscript(string text, string source, string? status = null)
    {
        var etat = status ?? "capture";
        lock (_lock)
        {
            if (_transcripts.Count > 0 && etat == "partiel")
            {
                var derniere = _transcripts[^1];
                if (derniere.Status == "partiel")
                {
                    // Le texte en cours se met à jour sur place au lieu d'empiler.
                    derniere.Timestamp = DateTimeOffset.Now;
                    derniere.Text = text;
                    return;
                }
            }
            _transcripts.Add(new VoiceTranscriptItem
            {
                Timestamp = DateTimeOffset.Now,
                Text = text,
                Source = source,
                Status = etat
            });
            while (_transcripts.Count > MaxTranscripts)
                _transcripts.RemoveAt(0);
        }
    }

    public IReadOnlyList<VoiceTranscriptItem> GetTranscripts(int? limit = null)
    {
        lock (_lock)
        {
            var take = limit ?? _transcripts.Count;
            return _transcripts.Skip(Math.Max(0, _transcripts.Count - take)).ToArray();
        }
    }

    public VoiceStatusSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new VoiceStatusSnapshot
            {
                EngineActive = _engineActive,
                EngineState = _engineState,
                EngineMessage = _engineMessage,
                CaptureMode = _captureMode,
                MicroState = _microState,
                WakeWordState = _wakeWordState,
                SttState = _sttState,
                TtsState = _ttsState,
                ListeningState = _listeningState,
                Ambient = _ambient,
                StartedAt = _startedAt,
                Transcripts = _transcripts.ToArray()
            };
        }
    }
}

public sealed class VoiceStatusSnapshot
{
    public bool EngineActive { get; set; }
    public string EngineState { get; set; } = "idle";
    public string EngineMessage { get; set; } = "";
    public string CaptureMode { get; set; } = "inconnu";
    public string MicroState { get; set; } = "inconnu";

    /// <summary>Niveau micro instantané (RMS 0-1) pour la waveform du HUD.</summary>
    public double LastRms { get; set; }
    public string WakeWordState { get; set; } = "inconnu";
    public string SttState { get; set; } = "inconnu";
    public string TtsState { get; set; } = "inconnu";
    public string ListeningState { get; set; } = "inconnu";
    public string Ambient { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public IReadOnlyList<VoiceTranscriptItem> Transcripts { get; set; } = Array.Empty<VoiceTranscriptItem>();
}

public sealed class VoiceTranscriptItem
{
    public DateTimeOffset Timestamp { get; set; }
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
    public string Status { get; set; } = "capture";
}
