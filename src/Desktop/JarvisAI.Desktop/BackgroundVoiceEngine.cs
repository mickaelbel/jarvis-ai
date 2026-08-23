using JarvisAI.Application.Voice;
using JarvisAI.Web.Services;
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
    private Task _playbackTask = Task.CompletedTask;

    // Wake-word (détection audio avant STT)
    private bool _wakeArmed = true;
    private DateTime _lastWakeAt = DateTime.MinValue;
    private static readonly TimeSpan FollowUpWindow = TimeSpan.FromSeconds(8);

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
        IWakeWordDetector? wakeWord = null)
    {
        _voice = voice;
        _settings = settings;
        _status = status;
        _ambient = ambient ?? new AmbientContextService();
        _wakeWord = wakeWord;
        _voice.StateChanged += OnStateChanged;
        _voice.AudioForPlayback += OnAudioForPlayback;
        _voice.UtteranceProcessed += OnUtteranceProcessed;
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
                    _status.EngineState = "error";
                    _status.EngineMessage = "Aucun micro disponible, nouvelle tentative dans 15 s";
                    App.Log("[VoiceEngine] No usable mic, retrying in 15s");
                    for (var i = 0; i < 75 && _running && !_disposed; i++)
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
                _status.EngineMessage = $"Micro actif ({rate} Hz, {channels} can.)";
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
        _status.EngineState = "error";
        _status.MicroState = "indisponible";
        _status.ListeningState = "inactif";
        _status.EngineMessage = "Aucun format de micro n'a pu être ouvert";
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
            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

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
                _status.EngineMessage = $"Micro WASAPI actif ({capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} can.)";
            App.Log($"[VoiceEngine] WASAPI capture started: {capture.WaveFormat.SampleRate}Hz/{capture.WaveFormat.Channels}ch");
            return true;
        }
        catch (Exception ex)
        {
            if (capture is not null) { try { capture.Dispose(); } catch { } }
            CleanupWasapi();
            App.Log($"[VoiceEngine] WASAPI unavailable, falling back to WaveIn: {ex.Message}");
            return false;
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

            if (rms < _noiseFloor) _noiseFloor = _noiseFloor * 0.95 + rms * 0.05;
            else _noiseFloor *= 0.9995;

            var settings = _settings.Get();
            // Push-to-talk actif : seuil abaissé pour capter même à voix basse.
            var pttActive = DateTime.UtcNow < _pttUntil;
            var quietFactor = _quietSeconds > 12 ? 0.25 : _quietSeconds > 6 ? 0.5 : _quietSeconds > 2 ? 0.75 : 1.0;
            var threshold = pttActive
                ? Math.Max(_noiseFloor * 1.2, settings.VadThreshold * 0.35)
                : Math.Max(_noiseFloor * 3, settings.VadThreshold * quietFactor);
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

            // Barge-in: stop playback + cancel the AI while the user speaks.
            if (_speaking && settings.BargeInEnabled && isSpeech)
            {
                StopPlayback();
                _voice.Interrupt();
                return;
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
                }
                if (_recording)
                {
                    AppendBytes(pcm);
                    var maxBytes = settings.MaxUtteranceSeconds * sampleRate * 2;
                    if (_utteranceBytes >= maxBytes)
                        FlushUtterance();
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

                var detection = await _wakeWord.DetectAsync(bytes, sampleRate);

                // Anti-faux-réveil adaptatif : en environnement parlant (TV,
                // conversation), on exige un score nettement plus élevé.
                var noisy = ComputeAmbientSpeechDensity() > 0.5;
                var required = 0.5 * (noisy ? 1.35 : 1.05);
                if (!detection.Triggered || detection.Score < required)
                {
                    _status.WakeWordState = noisy ? "écoute (mot-clé, bruit filtré)" : "écoute (mot-clé)";
                    return;
                }

                _wakeArmed = false;
                _lastWakeAt = DateTime.UtcNow;
                _status.WakeWordState = "éveillé";
                App.Log($"[VoiceEngine] Mot-clé détecté (score={detection.Score:F2}, bruit={noisy})");
            }
        }

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
    private void FlushUtterance()
    {
        if (!_recording) return;
        _recording = false;
        _rmsAbove = 0;
        _silenceFrames = 0;
        var bytes = _utterance.ToArray();
        var sampleRate = (int)_captureRate;
        _utterance.Clear();
        _utteranceBytes = 0;

        if (bytes.Length < 3200) return;

        var force = DateTime.UtcNow < _pttUntil;
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
            _playbackTask = _playbackTask.ContinueWith(_ => PlayWav(wav));
        }
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
            _status.TtsState = "prêt";
            _status.EngineState = "listening";
            _status.ListeningState = "écoute active";
        }
    }

    private void StopPlayback()
    {
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
