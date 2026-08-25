using JarvisAI.Application.Voice;
using JarvisAI.Web.Services;
using System.Net.Http;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace JarvisAI.Desktop;

public sealed class BackgroundVoiceEngine : IDisposable
{
    private readonly VoiceConversationService _voice;
    private readonly IVoiceSettingsStore _settings;
    private readonly AppVoiceStatus _status;
    private readonly AmbientContextService _ambient;
    private readonly IWakeWordDetector? _wakeWord;
    private readonly Application.Services.IReminderService? _reminders;
    private System.Threading.Timer? _reminderTimer;
    private DateTime _lastAmbientFeed = DateTime.MinValue;

    private readonly object _lock = new();
    private WaveInEvent? _waveIn;
    private WasapiCapture? _wasapiCapture;
    private MMDeviceEnumerator? _mmEnumerator;
    private bool _running;
    private bool _capturing;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private volatile bool _captureFailed;
    private string _activeMicId = "";

    // VAD state (mirrors voice.js)
    private bool _recording;
    private int _rmsAbove;
    private int _silenceFrames;
    private int _silenceFramesReq = 3;
    private readonly List<byte> _utterance = new();
    private int _utteranceBytes;
    private double _noiseFloor = 0.002;
    private double _quietSeconds;
    private float _captureRate = 44100;
    private int _channels = 1;
    private float _frameSeconds = 0.1f;

    // Playback
    private WaveOutEvent? _waveOut;
    private bool _speaking;
    // AEC heuristique (sans AEC matérielle) : niveau moyen de l'écho de notre
    // propre TTS renvoyé dans le micro, mesuré en continu pendant la lecture.
    private float _echoRms;
    private float _echoSum;
    private int _echoCount;
    private int _bargeFrames;
    private Task _playbackTask = Task.CompletedTask;
    // Génération de lecture : incrémentée à chaque interruption, elle invalide
    // tous les morceaux audio encore en file (sinon l'ancienne réponse coupée
    // repart après la nouvelle — d'où des réponses « doublées »).
    private int _playbackGeneration;

    // Wake-word (détection audio avant STT)
    private bool _wakeArmed = true;
    private DateTime _lastWakeAt = DateTime.MinValue;
    private static readonly TimeSpan FollowUpWindow = TimeSpan.FromSeconds(30);
    // Annonce unique « Modèle chargé » dès que le wake word est prêt.
    private bool _modeleChargeAnnonce;
    // Énergie crête de l'énoncé en cours (anti-hallucination Whisper).
    private double _peakRmsUtterance;
    // Début de la lecture TTS en cours (période de grâce du barge-in).
    private DateTime _debutLecture = DateTime.UtcNow;
    // Fin de la dernière lecture TTS : cooldown anti-écho (le micro capte la
    // queue de la voix / le souffle BT et Whisper hallucine un réveil).
    private DateTime _finLectureUtc = DateTime.MinValue;

    // File d'attente des énoncés : la détection wake-word et le STT tournent
    // dans un thread dédié, le thread de capture n'est JAMAIS bloqué (P2-7).
    private readonly System.Collections.Concurrent.BlockingCollection<(byte[] Pcm, int Rate, bool Force)> _utteranceQueue = new(8);
    private Thread? _gateThread;

    // Push-to-talk (Ctrl+Alt+J) : fenêtre d'écoute forcée sans mot-clé.
    private DateTime _pttUntil = DateTime.MinValue;

    // Densité de parole ambiante sur les ~30 dernières secondes (anti-faux-réveil).
    private readonly bool[] _speechRing = new bool[300];
    private int _speechRingIndex;

    private static readonly (int Rate, int Channels)[] CaptureFormats =
    {
        (44100, 1),
        (16000, 1),
        (48000, 2)
    };

    public BackgroundVoiceEngine(
        VoiceConversationService voice,
        IVoiceSettingsStore settings,
        AppVoiceStatus status,
        AmbientContextService? ambient = null,
        IWakeWordDetector? wakeWord = null,
        Application.Services.IReminderService? reminders = null)
    {
        _voice = voice;
        _settings = settings;
        _status = status;
        _ambient = ambient ?? new AmbientContextService();
        _wakeWord = wakeWord;
        _reminders = reminders;
        _voice.StateChanged += OnStateChanged;
        _voice.AudioForPlayback += OnAudioForPlayback;
        _voice.UtteranceProcessed += OnUtteranceProcessed;
        _voice.DictationInjected += OnDictationInjected;
    }

    /// <summary>
    /// Starts the reconcile loop. The engine captures the mic, runs VAD, feeds the
    /// VoiceConversationService (STT + LLM + TTS) and plays responses through NAudio,
    /// independently of any browser page.
    /// </summary>
    public Task StartAsync()
    {
        _disposed = false;
        _gateThread = new Thread(GateLoop)
        {
            IsBackground = true,
            Name = "JarvisVoiceGate"
        };
        _gateThread.Start();
        _ = ReconcileLoopAsync();

        // Rappels vocaux : vérifie toutes les 30 s les rappels échus.
        if (_reminders is not null)
        {
            _reminderTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var due = _reminders.GetDueNow();
                    foreach (var r in due)
                    {
                        _reminders.MarkFired(r.Id);
                        var message = string.IsNullOrWhiteSpace(r.Message) ? r.Title : r.Message;
                        App.Log($"[Reminder] Rappel échu : {r.Title}");
                        _ = _voice.SpeakAsync($"Rappel : {message}");
                    }
                }
                catch { }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Push-to-talk (Ctrl+Alt+J) : 10 s d'écoute forcée — seuil VAD abaissé et
    /// mot-clé contourné, idéal quand l'environnement est bruyant.
    /// </summary>
    public void TriggerPushToTalk()
    {
        lock (_lock)
        {
            _pttUntil = DateTime.UtcNow.AddSeconds(10);
            if (!_running) return;
            _status.EngineMessage = "Push-to-talk : j'écoute (10 s)";
            App.Log("[VoiceEngine] Push-to-talk activé");
        }
    }

    public void Stop()
    {
        _reminderTimer?.Dispose();
        lock (_lock)
        {
            _disposed = true;
            _running = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _recording = false;
            _utterance.Clear();
            StopPlayback();
        }
        _status.EngineActive = false;
        _status.EngineState = "idle";
        _status.MicroState = "arrêté";
        _status.WakeWordState = "inactif";
        _status.SttState = "inactif";
        _status.TtsState = "inactif";
        _status.ListeningState = "inactif";
        App.Log("[VoiceEngine] Stopped");
    }

    public void Dispose()
    {
        Stop();
        _voice.StateChanged -= OnStateChanged;
        _voice.AudioForPlayback -= OnAudioForPlayback;
        _voice.UtteranceProcessed -= OnUtteranceProcessed;
    }

    private async Task ReconcileLoopAsync()
    {
        while (!_disposed)
        {
            try
            {
                await Task.Delay(2000);
            }
            catch { return; }

            Reconcile();
        }
    }

    private void Reconcile()
    {
        lock (_lock)
        {
            if (_disposed) return;
            var settings = _settings.Get();
            if (settings.VoiceEnabled && !_running)
            {
                StartCaptureLocked(settings);
                _activeMicId = settings.MicDeviceId ?? "";
            }
            else if (!settings.VoiceEnabled && _running)
            {
                StopLocked("Voix désactivée dans les paramètres");
            }
            else if (settings.VoiceEnabled && _running && _capturing
                     && !string.Equals(settings.MicDeviceId ?? "", _activeMicId, StringComparison.OrdinalIgnoreCase))
            {
                App.Log("[VoiceEngine] Micro device changed — restarting capture");
                StopLocked("Micro changé");
                StartCaptureLocked(settings);
                _activeMicId = settings.MicDeviceId ?? "";
            }

            // The engine is "active" only while audio is actually captured, so the
            // browser voice stays usable as a fallback when the app mic fails.
            _status.EngineActive = _running && _capturing;
            if (_status.EngineActive)
            {
                // Reflète le réglage réel du mot-clé (sinon l'UI affiche un état périmé).
                _status.WakeWordState = settings.WakeWordEnabled
                    ? "continu (avec mot-clé)"
                    : "continu (sans mot-clé)";
                _status.Ambient = _ambient.GetContextSummary();
            }
            if (_capturing && _status.StartedAt is null)
            {
                _status.StartedAt = DateTime.UtcNow;
            }
        }
    }

    private void StartCaptureLocked(VoiceSettings settings)
    {
        _running = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _captureThread = new Thread(() => CaptureThreadMain(settings))
        {
            IsBackground = true,
            Name = "JarvisVoiceCapture"
        };
        _captureThread.SetApartmentState(ApartmentState.STA);
        _captureThread.Start();
        App.Log("[VoiceEngine] Capture thread started");
    }

    private void StopLocked(string reason)
    {
        _running = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _recording = false;
        _utterance.Clear();
        StopPlayback();

        _status.EngineActive = false;
        _status.EngineState = "idle";
        _status.MicroState = "arrêté";
        _status.WakeWordState = "inactif";
        _status.SttState = "inactif";
        _status.TtsState = "inactif";
        _status.ListeningState = "inactif";
        _status.EngineMessage = reason;
        App.Log($"[VoiceEngine] Stopped: {reason}");
    }

    private void CaptureThreadMain(VoiceSettings settings)
    {
        try
        {
            while (_running && !_disposed)
            {
                _capturing = false;
                _captureFailed = false;
                TryStartCapture(_settings.Get());

                if (!_capturing)
                {
                    _status.EngineState = "sans micro";
                    _status.CaptureMode = "aucun";
                    _status.MicroState = "désactivé (aucun micro détecté)";
                    _status.ListeningState = "en attente d'un micro";
                    _status.WakeWordState = "inactif";
                    _status.SttState = "en veille";
                    _status.EngineMessage = "Branche un micro : Jarvis le détectera tout seul (vérification toutes les 5 s).";
                    App.Log("[VoiceEngine] No usable mic, retrying in 5s");
                    for (var i = 0; i < 25 && _running && !_disposed; i++)
                        Thread.Sleep(200);
                    continue;
                }

                PumpUntilStopped();
                CleanupAll();
            }
        }
        catch (Exception ex)
        {
            App.Log("[VoiceEngine] Capture thread error: " + ex);
            _capturing = false;
            _status.EngineState = "error";
        }
        finally
        {
            CleanupAll();
        }
    }

    private void PumpUntilStopped()
    {
        var msg = new MSG();
        while (_running && !_captureFailed && !_disposed)
        {
            if (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
    }

    private void TryStartCapture(VoiceSettings settings)
    {
        if (TryStartWasapi(settings)) return;

        foreach (var (rate, channels) in CaptureFormats)
        {
            if (!_running || _disposed) return;
            try
            {
                _captureFailed = false;
                var device = SelectMicDevice(settings);
                var waveIn = new WaveInEvent
                {
                    DeviceNumber = device,
                    WaveFormat = new WaveFormat(rate, 16, channels),
                    BufferMilliseconds = 100,
                    NumberOfBuffers = 3
                };
                waveIn.DataAvailable += OnDataAvailable;
                waveIn.RecordingStopped += OnRecordingStopped;
                waveIn.StartRecording();
                _waveIn = waveIn;

                // Pump messages briefly so NAudio can fail loudly if the device
                // rejects this format; otherwise keep it.
                var probe = Stopwatch.StartNew();
                while (probe.ElapsedMilliseconds < 400 && !_captureFailed)
                {
                    PumpOnce();
                    Thread.Sleep(10);
                }

                if (_captureFailed)
                {
                    App.Log($"[VoiceEngine] Format {rate}Hz/{channels}ch rejected, trying next");
                    CleanupWaveIn();
                    continue;
                }

                _captureRate = rate;
                _channels = channels;
                _capturing = true;
                _frameSeconds = 0.1f;
                _micActifNom = $"WaveIn #{device}";
                _debutCapture = DateTime.UtcNow;
                _sonVuDepuisDebut = false;
                Interlocked.Exchange(ref _basculeEnCours, 0);
                var silenceMs = settings.SilenceTimeoutMs > 0 ? settings.SilenceTimeoutMs : 800;
                _silenceFramesReq = Math.Max(1, (int)Math.Ceiling(silenceMs / 1000.0 / _frameSeconds));
                _status.EngineState = "listening";
                _status.CaptureMode = "WaveIn";
                _status.MicroState = "actif";
                _status.ListeningState = "écoute active";
                _status.WakeWordState = settings.WakeWordEnabled
                    ? "continu (avec mot-clé)"
                    : "continu (sans mot-clé)";
                _status.SttState = "prêt";
                _status.TtsState = "prêt";
                _status.EngineMessage = $"Micro actif : {_micActifNom} ({rate} Hz, {channels} can.)";
                App.Log($"[VoiceEngine] Capture started: {rate}Hz/{channels}ch (device={device})");
                return;
            }
            catch (Exception ex)
            {
                App.Log($"[VoiceEngine] Capture init failed at {rate}Hz/{channels}ch: {ex.Message}");
                CleanupWaveIn();
            }
        }

        _capturing = false;
        _status.EngineState = "sans micro";
        _status.MicroState = "désactivé (aucun micro détecté)";
        _status.ListeningState = "en attente d'un micro";
        _status.EngineMessage = "Branche un micro : Jarvis le détectera tout seul.";
    }

    private void PumpOnce()
    {
        var msg = new MSG();
        while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT) return;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void CleanupWaveIn()
    {
        var w = _waveIn;
        _waveIn = null;
        if (w is not null)
        {
            try { w.StopRecording(); } catch { }
            try { w.DataAvailable -= OnDataAvailable; } catch { }
            try { w.RecordingStopped -= OnRecordingStopped; } catch { }
            try { w.Dispose(); } catch { }
        }
    }

    private void CleanupWasapi()
    {
        var w = _wasapiCapture;
        _wasapiCapture = null;
        if (w is not null)
        {
            try { w.DataAvailable -= OnWasapiData; } catch { }
            try { w.RecordingStopped -= OnRecordingStopped; } catch { }
            try { w.StopRecording(); } catch { }
            try { w.Dispose(); } catch { }
        }
        var en = _mmEnumerator;
        _mmEnumerator = null;
        if (en is not null)
        {
            try { en.Dispose(); } catch { }
        }
    }

    private void CleanupAll()
    {
        CleanupWaveIn();
        CleanupWasapi();
    }

    /// <summary>
    /// Capture WASAPI en mode partagé : c'est le chemin primaire (robuste face à la
    /// concurrence du navigateur, faible latence). Si la machine n'expose aucune
    /// interface WASAPI, on retombe sur WaveInEvent (MME).
    /// </summary>
    private bool TryStartWasapi(VoiceSettings settings)
    {
        WasapiCapture? capture = null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            if (!string.IsNullOrWhiteSpace(settings.MicDeviceId))
            {
                device = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName?.Contains(settings.MicDeviceId, StringComparison.OrdinalIgnoreCase) == true);
            }
            var actifs = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            // Priorité : réglage explicite > bascule auto anti-muet > défaut Windows > premier actif.
            device ??= (string.IsNullOrWhiteSpace(_micPrefere)
                    ? null
                    : actifs.FirstOrDefault(d => d.FriendlyName == _micPrefere))
                ?? SafeDefaultCapture(enumerator)
                ?? actifs.FirstOrDefault();

            capture = new WasapiCapture(device);
            capture.DataAvailable += OnWasapiData;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            _mmEnumerator = enumerator;
            _wasapiCapture = capture;
            _captureRate = capture.WaveFormat.SampleRate;
            _channels = capture.WaveFormat.Channels;
            _frameSeconds = 0.1f;
            _captureFailed = false;
            _capturing = true;
            _micActifNom = device?.FriendlyName ?? "inconnu";
            _debutCapture = DateTime.UtcNow;
            _sonVuDepuisDebut = false;
            Interlocked.Exchange(ref _basculeEnCours, 0);
            var silenceMs = settings.SilenceTimeoutMs > 0 ? settings.SilenceTimeoutMs : 800;
            _silenceFramesReq = Math.Max(1, (int)Math.Ceiling(silenceMs / 1000.0 / _frameSeconds));
                _status.EngineState = "listening";
                _status.CaptureMode = "WASAPI";
                _status.MicroState = "actif";
                _status.ListeningState = "écoute active";
                _status.WakeWordState = settings.WakeWordEnabled
                    ? "continu (avec mot-clé)"
                    : "continu (sans mot-clé)";
                _status.SttState = "prêt";
                _status.TtsState = "prêt";
                _status.EngineMessage = $"Micro WASAPI actif : {_micActifNom} ({capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} can.)";
            App.Log($"[VoiceEngine] WASAPI capture started: {_micActifNom} @ {capture.WaveFormat.SampleRate}Hz/{capture.WaveFormat.Channels}ch");
            return true;
        }
        catch (Exception ex)
        {
            if (capture is not null) { try { capture.Dispose(); } catch { } }
            CleanupWasapi();
            App.Log($"[VoiceEngine] WASAPI indisponible, repli WaveIn : {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    /// <summary>Default endpoint de capture, tolérant aux machines sans défaut.</summary>
    private static MMDevice? SafeDefaultCapture(MMDeviceEnumerator enumerator)
    {
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
        catch { return null; }
    }

    /// <summary>
    /// Si le micro actif ne renvoie que du silence numérique depuis son ouverture
    /// (périphérique virtuel muet choisi par Windows), bascule vers un autre
    /// périphérique actif — en préférant un nom non virtuel — et relance la capture.
    /// </summary>
    private void BasculerVersMicroVivant()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var actifs = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            var autres = actifs.Where(d => d.FriendlyName != _micActifNom).ToList();
            if (autres.Count == 0) return;

            string[] virtuels = { "droidcam", "steam", "vb-audio", "voicemeeter", "cable", "virtual", "obs" };
            var cible = autres.FirstOrDefault(d =>
                    !virtuels.Any(v => (d.FriendlyName ?? "").ToLowerInvariant().Contains(v)))
                ?? autres[0];

            _micPrefere = cible.FriendlyName;
            App.Log($"[VoiceEngine] Micro '{_micActifNom}' muet depuis l'ouverture → bascule vers '{_micPrefere}'");
            _status.EngineMessage = $"Micro « {_micActifNom} » silencieux → essai de « {_micPrefere} »…";
            // Sort PumpUntilStopped : la boucle de capture relance TryStartCapture.
            _captureFailed = true;
        }
        catch (Exception ex)
        {
            App.Log("[VoiceEngine] Bascule micro impossible : " + ex.Message);
            Interlocked.Exchange(ref _basculeEnCours, 0);
        }
    }

    /// <summary>
    /// Conversion générique → PCM16 mono pour le pipeline VAD/STT, quel que soit le
    /// format livré par WASAPI (IeeeFloat 32 bits, PCM 16/24/32 bits, n canaux).
    /// </summary>
    private static byte[] ConvertToMonoPcm16(byte[] buffer, int bytes, WaveFormat wf)
    {
        if (wf is null || wf.Channels < 1 || wf.BlockAlign < 1 || bytes < wf.BlockAlign) return Array.Empty<byte>();
        var frames = bytes / wf.BlockAlign;
        if (frames <= 0) return Array.Empty<byte>();
        var outBytes = new byte[frames * 2];

        if (wf.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var stride = 4 * wf.Channels;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0.0;
                for (var c = 0; c < wf.Channels; c++)
                    sum += BitConverter.ToSingle(buffer, i * stride + c * 4);
                var mono = Math.Clamp(sum / wf.Channels, -1.0, 1.0);
                var s = (short)(mono * 32767.0);
                outBytes[i * 2] = (byte)(s & 0xFF);
                outBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return outBytes;
        }

        if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 16)
        {
            var stride = 2 * wf.Channels;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0;
                for (var c = 0; c < wf.Channels; c++)
                {
                    var off = i * stride + c * 2;
                    sum += (short)(buffer[off] | (buffer[off + 1] << 8));
                }
                var mono = (short)(sum / wf.Channels);
                outBytes[i * 2] = (byte)(mono & 0xFF);
                outBytes[i * 2 + 1] = (byte)((mono >> 8) & 0xFF);
            }
            return outBytes;
        }

        if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 32)
        {
            var stride = 4 * wf.Channels;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0L;
                for (var c = 0; c < wf.Channels; c++)
                    sum += BitConverter.ToInt32(buffer, i * stride + c * 4);
                var mono = Math.Clamp(sum / wf.Channels, short.MinValue, short.MaxValue);
                outBytes[i * 2] = (byte)(mono & 0xFF);
                outBytes[i * 2 + 1] = (byte)((mono >> 8) & 0xFF);
            }
            return outBytes;
        }

        if (wf.Encoding == WaveFormatEncoding.Pcm && wf.BitsPerSample == 24)
        {
            var stride = 3 * wf.Channels;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0L;
                for (var c = 0; c < wf.Channels; c++)
                {
                    var off = i * stride + c * 3;
                    var v = (int)(buffer[off] | (buffer[off + 1] << 8) | (buffer[off + 2] << 16));
                    if ((buffer[off + 2] & 0x80) != 0) v |= unchecked((int)0xFF000000);
                    sum += v;
                }
                var mono = Math.Clamp((sum / wf.Channels) >> 8, short.MinValue, short.MaxValue);
                outBytes[i * 2] = (byte)(mono & 0xFF);
                outBytes[i * 2 + 1] = (byte)((mono >> 8) & 0xFF);
            }
            return outBytes;
        }

        return Array.Empty<byte>();
    }

    private void OnWasapiData(object? sender, WaveInEventArgs e)
    {
        var wf = _wasapiCapture?.WaveFormat;
        if (wf is null) return;
        var pcm = ConvertToMonoPcm16(e.Buffer, e.BytesRecorded, wf);
        if (pcm.Length < 4) return;
        ProcessPcm16Chunk(pcm, wf.SampleRate);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded < 4) return;
        var raw = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        var pcm = _channels == 1 ? raw : DownmixToMono(raw);
        ProcessPcm16Chunk(pcm, (int)_captureRate);
    }

    private void ProcessPcm16Chunk(byte[] pcm, int sampleRate)
    {
        lock (_lock)
        {
            if (_disposed || _cts is null || _cts.IsCancellationRequested) return;
            if (pcm.Length < 4) return;

            var sampleCount = pcm.Length / 2;
            var sum = 0.0;
            for (var i = 0; i < pcm.Length; i += 2)
            {
                var s = (short)(pcm[i] | (pcm[i + 1] << 8));
                var v = s / 32768.0;
                sum += v * v;
            }
            var rms = Math.Sqrt(sum / sampleCount);
            _status.LastRms = rms; // waveform HUD
            _status.NoiseFloor = _noiseFloor; // jauge adaptative

            // Watchdog micro muet : aucun son réel depuis l'ouverture de la
            // capture et 25 s écoulées → on essaie un autre périphérique.
            if (rms > 1e-4) _sonVuDepuisDebut = true;
            else if (!_sonVuDepuisDebut && !_recording && !_disposed
                     && Interlocked.CompareExchange(ref _basculeEnCours, 1, 0) == 0
                     && (DateTime.UtcNow - _debutCapture).TotalSeconds > 25)
            {
                Task.Run(BasculerVersMicroVivant);
            }

            if (rms < _noiseFloor) _noiseFloor = _noiseFloor * 0.95 + rms * 0.05;
            else _noiseFloor *= 0.9995;

            var settings = _settings.Get();
            // Push-to-talk actif : seuil abaissé pour capter même à voix basse.
            var pttActive = DateTime.UtcNow < _pttUntil;

            // ── Seuil VAD adaptatif ────────────────────────────────────────
            // Au lieu d'utiliser VadThreshold (fixe à 0.02), on calibre le
            // seuil sur le bruit ambiant réel : seuil = noiseFloor × 2.5,
            // borné entre 0.003 et le settings pour ne pas déraper.
            // Ça couvre les micros BT à faible gain (RMS ~0.005) ET les
            // micros studio (RMS ~0.03+).
            var adaptiveVad = Math.Clamp(_noiseFloor * 2.5, 0.003, Math.Max(settings.VadThreshold, 0.04));
            var quietFactor = _quietSeconds > 12 ? 0.55 : _quietSeconds > 6 ? 0.7 : _quietSeconds > 2 ? 0.85 : 1.0;
            var threshold = pttActive
                ? Math.Max(_noiseFloor * 1.2, adaptiveVad * 0.35)
                : Math.Max(Math.Max(_noiseFloor * 1.8, adaptiveVad * quietFactor), 0.002);
            var isSpeech = rms > threshold;

            // Densité de parole ambiante (fenêtre glissante ~30 s) — sert au
            // filtrage adaptatif des faux réveils du mot-clé.
            lock (_speechRing)
            {
                _speechRing[_speechRingIndex] = isSpeech;
                _speechRingIndex = (_speechRingIndex + 1) % _speechRing.Length;
            }

            // Analyse d'ambiance continue (contexte du moment, même sans s'adresser
            // à Jarvis). On n'alimente pas pendant notre propre TTS pour ne pas
            // classer notre voix comme une voix environnante.
            if (!_speaking && DateTime.UtcNow - _lastAmbientFeed >= TimeSpan.FromMilliseconds(300))
            {
                _lastAmbientFeed = DateTime.UtcNow;
                _ambient.Feed(rms, isSpeech, DateTime.Now);
            }

            // Barge-in mains libres : le micro reste ouvert pendant que Jarvis
            // parle. Sans AEC matérielle, on calibre l'écho de notre propre voix
            // (RMS moyen mesuré pendant la lecture) et on n'interrompt que si
            // une source nettement plus forte parle de façon soutenue (~300 ms).
            // Sensibilité volontairement basse : Jarvis ne doit JAMAIS se
            // couper lui-même à cause d'une fuite de son ou du souffle BT.
            if (_speaking && settings.BargeInEnabled)
            {
                _echoSum += (float)rms;
                _echoCount++;
                if (_echoCount >= 25)
                {
                    _echoRms = _echoSum / _echoCount;
                    _echoSum = 0;
                    _echoCount = 0;
                }

                var echoGate = Math.Max((float)_noiseFloor * 4f, _echoRms * 2.2f);
                var gracePeriod = DateTime.UtcNow - _debutLecture > TimeSpan.FromSeconds(1);
                if (gracePeriod && rms > echoGate) _bargeFrames++; else if (!gracePeriod || rms <= echoGate * 0.8) _bargeFrames = Math.Max(0, _bargeFrames - 2);

                if (_bargeFrames >= 10)
                {
                    _bargeFrames = 0;
                    StopPlayback();
                    _voice.Interrupt();
                    return;
                }
                return; // jamais d'enregistrement d'énoncé pendant qu'on parle
            }

            if (isSpeech)
            {
                _quietSeconds = 0;
                _rmsAbove++;
                _silenceFrames = 0;
                if (_rmsAbove >= 2 && !_recording)
                {
                    _recording = true;
                    _utterance.Clear();
                    _utteranceBytes = 0;
                    _peakRmsUtterance = rms;
                }
                if (_recording)
                {
                    if (rms > _peakRmsUtterance) _peakRmsUtterance = rms;
                    AppendBytes(pcm);
                    var maxBytes = settings.MaxUtteranceSeconds * sampleRate * 2;
                    if (_utteranceBytes >= maxBytes)
                        FlushUtterance();
                    else
                        MaybeTranscribePartiellement(sampleRate);
                }
            }
            else if (_recording)
            {
                _quietSeconds += _frameSeconds;
                _silenceFrames++;
                if (_silenceFrames >= _silenceFramesReq)
                    FlushUtterance();
            }
            else
            {
                _quietSeconds += _frameSeconds;
            }
        }
    }

    private void AppendBytes(byte[] data)
    {
        for (var i = 0; i < data.Length; i++)
            _utterance.Add(data[i]);
        _utteranceBytes += data.Length;
    }

    /// <summary>
    /// Thread consommateur de la file d'énoncés : wake-word puis STT/LLM/TTS.
    /// Toute la latence réseau (serveurs Python) vit ici, pas sur le micro.
    /// </summary>
    private void GateLoop()
    {
        foreach (var item in _utteranceQueue.GetConsumingEnumerable())
        {
            if (_disposed) return;
            try
            {
                GateProcessAsync(item.Pcm, item.Rate, item.Force).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                App.Log("[VoiceEngine] Gate error: " + ex.Message);
            }
        }
    }

    private async Task GateProcessAsync(byte[] bytes, int sampleRate, bool force)
    {
        // Mono-tâche : pendant qu'une action/réponse est en cours, tout nouvel
        // input vocal est ignoré (sauf push-to-talk). Fini le spam parallèle.
        if (!force && _voice.State != Application.Voice.VoiceState.Idle)
        {
            App.Log($"[VoiceEngine] Input ignoré : Jarvis est occupé ({_voice.State})");
            return;
        }

        // Cooldown post-lecture : dans les 2 s qui suivent la fin du TTS,
        // le micro capte encore la queue de la voix → on ignore (anti-boucle).
        if (!force && (DateTime.UtcNow - _finLectureUtc).TotalSeconds < 2)
        {
            App.Log("[VoiceEngine] Input ignoré : cooldown post-lecture TTS");
            return;
        }

        var settings = _settings.Get();

        // Vrai wake-word : avant de dépenser du STT, on vérifie le mot-clé sur
        // l'audio (coût quasi nul). Une fois éveillé, la commande qui suit part
        // directe. Le push-to-talk contourne tout le gate.
        if (!force && settings.WakeWordEnabled && _wakeWord is not null)
        {
            if (!_wakeArmed)
            {
                if (DateTime.UtcNow - _lastWakeAt > FollowUpWindow)
                {
                    _wakeArmed = true;
                    return;
                }
            }
            else
            {
                if (!await _wakeWord.EnsureStartedAsync())
                {
                    _status.WakeWordState = "indisponible";
                    return;
                }
                if (!_modeleChargeAnnonce)
                {
                    _modeleChargeAnnonce = true;
                    _status.EngineMessage = $"Modèle chargé — Jarvis vous écoute ({_micActifNom})";
                    App.Log("[VoiceEngine] Modèle chargé — écoute active");
                }

                var detection = await _wakeWord.DetectAsync(bytes, sampleRate);

                // Anti-faux-réveil adaptatif : en environnement parlant (TV,
                // conversation), on exige un score nettement plus élevé.
                var noisy = ComputeAmbientSpeechDensity() > 0.5;
                var required = 0.5 * (noisy ? 1.35 : 1.05);
                if (!detection.Triggered || detection.Score < required)
                {
                    _status.WakeWordState = noisy ? "écoute (mot-clé, bruit filtré)" : "écoute (mot-clé)";
                    // Fallback texte : le modèle acoustique rate souvent le mot-clé
                    // selon la prononciation/distance. On laisse le pipeline complet
                    // vérifier le mot-clé SUR LA TRANSCRIPTION (il ignore proprement
                    // les phrases sans « jarvis ») au lieu de jeter l'audio.
                    App.Log($"[VoiceEngine] Gate acoustique raté (score={detection.Score:F2}) → vérification par STT");
                    try { await _voice.ProcessUtteranceAsync(bytes, sampleRate); }
                    catch { }
                    finally { RestoreListeningStates(); }
                    return;
                }

                _wakeArmed = false;
                _lastWakeAt = DateTime.UtcNow;
                _status.WakeWordState = "éveillé";
                App.Log($"[VoiceEngine] Mot-clé détecté (score={detection.Score:F2}, bruit={noisy})");
            }
        }

        _voice.AmbientSpeechDensity = ComputeAmbientSpeechDensity();
        _status.EngineState = "processing";
        _status.SttState = "traitement";
        _status.ListeningState = "occupé";
        try
        {
            await _voice.ProcessUtteranceAsync(bytes, sampleRate);
        }
        catch (Exception ex)
        {
            App.Log("[VoiceEngine] ProcessUtterance error: " + ex);
            _status.EngineState = "error";
            _status.SttState = "erreur";
            _status.ListeningState = "inactif";
        }
        finally
        {
            RestoreListeningStates();
        }
    }

    /// <summary>
    /// Rend l'état d'écoute après un traitement : plusieurs chemins de
    /// ProcessUtteranceAsync (phrase sans mot-clé, commande vide, etc.)
    /// retournent sans repasser par Idle, laissant « traitement » affiché.
    /// </summary>
    private void RestoreListeningStates()
    {
        if (_speaking) return; // Jarvis parle : OnStateChanged remettra tout au retour à Idle
        if (_voice.State == Application.Voice.VoiceState.Speaking) return;
        if (_running && !_captureFailed)
        {
            _status.EngineState = "listening";
            _status.SttState = "prêt";
            _status.ListeningState = "écoute active";
        }
    }

    private double ComputeAmbientSpeechDensity()
    {
        lock (_speechRing)
        {
            var speech = 0;
            for (var i = 0; i < _speechRing.Length; i++)
                if (_speechRing[i]) speech++;
            return speech / (double)_speechRing.Length;
        }
    }

    private int SelectMicDevice(VoiceSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.MicDeviceId))
        {
            try
            {
                for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    if (caps.ProductName?.Contains(settings.MicDeviceId, StringComparison.OrdinalIgnoreCase) == true)
                        return i;
                }
            }
            catch { }
        }
        return 0;
    }

    /// <summary>
    /// Fin d'énoncé : on enfile les échantillons pour le gate (non bloquant,
    /// jamais plus de 8 énoncés en attente). Le thread de capture repart
    /// immédiatement écouter.
    /// </summary>
    // STT partiel : pendant que l'utilisateur parle, un extrait du buffer part
    // toutes les 2,5 s au serveur Whisper → le HUD affiche la phrase en direct.
    private DateTime _dernierPartiel = DateTime.MinValue;
    private int _partielEnCours;
    private string _dernierTextePartiel = "";
    private static readonly HttpClient _sttHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    // Watchdog anti-micro-muet : si le périphérique capté ne renvoie QUE du
    // silence numérique (DroidCam, Steam, câbles virtuels choisis par défaut
    // Windows…), on bascule automatiquement vers un autre périphérique actif.
    private string? _micPrefere;
    private string? _micActifNom;
    private DateTime _debutCapture = DateTime.UtcNow;
    private volatile bool _sonVuDepuisDebut;
    private int _basculeEnCours;

    private void MaybeTranscribePartiellement(int sampleRate)
    {
        if (_utteranceBytes < sampleRate * 2) return;                       // < 1 s : trop court
        if ((DateTime.UtcNow - _dernierPartiel).TotalMilliseconds < 2500) return;
        // Pas de STT partiel sur un énoncé sans énergie vocale réelle :
        // c'est ce qui générait les partiels hallucinés en continu.
        // Plancher adapté aux micros BT à faible gain (voix normale ≈ 0.005).
        if (_peakRmsUtterance < Math.Max(_settings.Get().VadThreshold * 0.8, 0.006)) return;
        if (Interlocked.CompareExchange(ref _partielEnCours, 1, 0) != 0) return;
        _dernierPartiel = DateTime.UtcNow;

        var pcm = _utterance.ToArray();
        var rate = sampleRate;
        Task.Run(async () =>
        {
            try
            {
                using var wav = new MemoryStream();
                WriteWavHeader(wav, pcm.Length, rate);
                wav.Write(pcm, 0, pcm.Length);
                using var contenu = new ByteArrayContent(wav.ToArray());
                contenu.Headers.TryAddWithoutValidation("Content-Type", "audio/wav");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var reponse = await _sttHttp.PostAsync("http://127.0.0.1:17001/transcribe", contenu, cts.Token);
                if (!reponse.IsSuccessStatusCode) return;
                var brut = await reponse.Content.ReadAsStringAsync(cts.Token);
                // Le serveur STT renvoie du JSON {"text":"..."} : on extrait le texte.
                string texte;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(brut);
                    texte = (doc.RootElement.TryGetProperty("text", out var elTexte)
                        ? elTexte.GetString()
                        : brut).Trim().Trim('"');
                }
                catch { texte = brut.Trim().Trim('"'); }
                if (texte.Length > 1 && !JarvisAI.Application.Voice.VoiceConversationService.EstHallucinationProbable(texte))
                {
                    var stable = texte.Equals(_dernierTextePartiel, StringComparison.OrdinalIgnoreCase);
                    _dernierTextePartiel = texte;
                    _voice.RaiseUserTranscriptPartial(texte);
                    _status.AddTranscript(texte, "interim STT", "partiel");

                    // Déclenchement précoce : deux transcriptions partielles
                    // identiques + ponctuation finale + pause (~0,5 s) = fin de
                    // phrase probable. La ponctuation évite de couper une hésitation.
                    var phraseFinie = texte.EndsWith('.') || texte.EndsWith('!') || texte.EndsWith('?');
                    if (stable && phraseFinie)
                    {
                        lock (_lock)
                        {
                            if (_recording && !_disposed
                                && _silenceFrames * _frameSeconds >= 0.5
                                && DateTime.UtcNow - _pttUntil > TimeSpan.Zero)
                            {
                                App.Log("[VoiceEngine] Fin anticipée : interim STT stable");
                                FlushUtterance();
                            }
                        }
                    }
                }
            }
            catch { /* STT partiel best-effort */ }
            finally { Interlocked.Exchange(ref _partielEnCours, 0); }
        });
    }

    private static void WriteWavHeader(MemoryStream flux, int pcmLength, int sampleRate)
    {
        var bw = new BinaryWriter(flux);
        bw.Write("RIFF"u8);
        bw.Write(36 + pcmLength);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(pcmLength);
    }

    private void FlushUtterance()
    {        if (!_recording) return;
        _recording = false;
        _rmsAbove = 0;
        _silenceFrames = 0;
        _dernierTextePartiel = "";
        var bytes = _utterance.ToArray();
        var sampleRate = (int)_captureRate;
        _utterance.Clear();
        _utteranceBytes = 0;
        var peak = _peakRmsUtterance;
        _peakRmsUtterance = 0;

        if (bytes.Length < 3200) return;

        // Anti-hallucination Whisper : un segment sans énergie vocale réelle
        // (souffle du casque, bruit de fond) produit des transcriptions
        // inventées qui faisaient parler Jarvis sans raison.
        // Plancher adapté aux micros BT à faible gain : le souffle du XM5
        // reste < 0.003, une voix normale est ≥ 0.004-0.006.
        var force = DateTime.UtcNow < _pttUntil;
        if (!force && peak < Math.Max(_settings.Get().VadThreshold * 0.8, 0.006))
        {
            App.Log($"[VoiceEngine] Énoncé rejeté : énergie insuffisante (peak RMS={peak:F4})");
            return;
        }

        if (!_utteranceQueue.TryAdd((bytes, sampleRate, force)))
            App.Log("[VoiceEngine] File d'énoncés saturée — énoncé ignoré");
    }

    private static byte[] DownmixToMono(byte[] bytes)
    {
        var frames = bytes.Length / 4;
        var outBytes = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var l = (short)(bytes[i * 4] | (bytes[i * 4 + 1] << 8));
            var r = (short)(bytes[i * 4 + 2] | (bytes[i * 4 + 3] << 8));
            var mono = (short)((l + r) / 2);
            outBytes[i * 2] = (byte)(mono & 0xFF);
            outBytes[i * 2 + 1] = (byte)((mono >> 8) & 0xFF);
        }
        return outBytes;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            App.Log("[VoiceEngine] Recording stopped with error: " + e.Exception.Message);
            lock (_lock)
            {
                _captureFailed = true;
                _capturing = false;
                if (!_disposed && _running)
                {
                    StopPlayback();
                    _status.EngineMessage = "Erreur micro : " + e.Exception.Message;
                }
            }
        }
        else
        {
            _capturing = false;
        }
    }

    private void OnStateChanged(VoiceState state)
    {
        _status.EngineState = state.ToString().ToLowerInvariant();
        _status.ListeningState = state == VoiceState.Processing ? "occupé" : "écoute active";
        if (state == VoiceState.Processing)
            _status.SttState = "traitement";
        else if (state == VoiceState.Idle)
            _status.SttState = "prêt";
    }

    private void OnUtteranceProcessed(JarvisAI.Application.Voice.VoiceUtteranceRecord record)
    {
        _status.AddTranscript(
            record.Transcript,
            record.WakeMatched ? "mot-clé + STT" : "STT",
            record.Status);
    }

    private void OnAudioForPlayback(byte[] wav)
    {
        lock (_lock)
        {
            var gen = _playbackGeneration;
            _playbackTask = _playbackTask.ContinueWith(_ =>
            {
                if (gen == Volatile.Read(ref _playbackGeneration)) PlayWav(wav);
            });
        }
    }

    // ── Dictée : injection dans l'app au premier plan ──────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private void OnDictationInjected(string text)
    {
        // Clipboard.SetText doit être appelé depuis le thread STA (Dispatcher).
        App.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                // Ctrl+V = VK_CONTROL (0x11) + VK_V (0x56)
                keybd_event(0x11, 0, 0, UIntPtr.Zero);
                keybd_event(0x56, 0, 0, UIntPtr.Zero);
                keybd_event(0x56, 0, 2, UIntPtr.Zero);
                keybd_event(0x11, 0, 2, UIntPtr.Zero);
                App.Log($"[Dictation] {text.Length} caractères injectés");
            }
            catch (Exception ex)
            {
                App.Log($"[Dictation] injection échouée : {ex.Message}");
            }
        });
    }

    private void PlayWav(byte[] wav)
    {
        if (!_running) return;
        try
        {
            using var ms = new MemoryStream(wav);
            using var reader = new WaveFileReader(ms);
            using var waveOut = new WaveOutEvent { Volume = Math.Clamp(_settings.Get().Volume, 0f, 1f) };
            _speaking = true;
            _debutLecture = DateTime.UtcNow;
            _waveOut = waveOut;
            _status.TtsState = "lecture";
            waveOut.Init(reader);
            waveOut.Play();
            while (waveOut.PlaybackState == PlaybackState.Playing && _running && _cts is { IsCancellationRequested: false })
            {
                Thread.Sleep(50);
            }
        }
        catch (Exception ex)
        {
            App.Log("[VoiceEngine] Playback error: " + ex);
        }
        finally
        {
            _speaking = false;
            _waveOut = null;
            _finLectureUtc = DateTime.UtcNow;
            _status.TtsState = "prêt";
            _status.EngineState = "listening";
            _status.ListeningState = "écoute active";
        }
    }

    private void StopPlayback()
    {
        Interlocked.Increment(ref _playbackGeneration); // jette la file audio en attente
        var waveOut = _waveOut;
        _waveOut = null;
        _speaking = false;
        if (waveOut is not null)
        {
            try { waveOut.Stop(); } catch { }
        }
    }

    // ─── Minimal Win32 message pump for the capture thread ──────────────────
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
}
