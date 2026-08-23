using JarvisAI.Application.AI;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Voice;

public enum VoiceState
{
    Idle,
    Listening,
    Processing,
    Speaking,
    Error
}

public sealed record VoiceUtteranceRecord(
    string Transcript,
    string Command,
    bool WakeMatched,
    string? Response,
    string Status);

public sealed class VoiceConversationService : IVoiceConfirmationChannel
{
    private readonly ISpeechToTextService _stt;
    private readonly ITextToSpeechService _tts;
    private readonly ITextToSpeechService? _ttsFallback;
    private readonly IAIService _aiService;
    private readonly IToolRegistry _toolRegistry;
    private readonly IVoiceSettingsStore _settingsStore;
    private readonly AmbientContextService _ambientContext;
    private readonly IVoiceModelPicker? _modelPicker;
    private readonly ConversationCondenser? _condenser;
    private readonly ILogger<VoiceConversationService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _condenseGate = new(1, 1);
    private readonly object _confirmationLock = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _ttsCts;
    private AIConversation? _sessionConversation;
    private DateTime _lastExchangeUtc = DateTime.MinValue;
    private TaskCompletionSource<VoiceConfirmationAnswer?>? _pendingConfirmation;

    private static readonly TimeSpan SessionResetTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AwaitingCommandTimeout = TimeSpan.FromSeconds(15);
    private const int MaxTtsChars = 1800;

    private static readonly string[] AffirmativePhrases =
    {
        "oui", "yes", "ok", "d'accord", "daccord", "vas-y", "vasy", "confirme", "confirmez",
        "valide", "go", "exact", "c'est bon", "cest bon", "bien sûr", "bien sur", "yep", "ouaip", "s'il vous plaît"
    };

    public event Action<VoiceState>? StateChanged;
    public event Action<string>? UserTranscript;
    public event Action<string>? ResponseGenerated;
    public event Action<byte[]>? AudioForPlayback;
    public event Action<string>? StatusMessage;
    public event Action<VoiceUtteranceRecord>? UtteranceProcessed;

    public VoiceState State { get; private set; } = VoiceState.Idle;

    private void SetState(VoiceState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public VoiceConversationService(
        ISpeechToTextService stt,
        ITextToSpeechService tts,
        IAIService aiService,
        IToolRegistry toolRegistry,
        IVoiceSettingsStore settingsStore,
        ILogger<VoiceConversationService> logger,
        ITextToSpeechService? ttsFallback = null,
        AmbientContextService? ambientContext = null,
        IVoiceModelPicker? modelPicker = null,
        ConversationCondenser? condenser = null)
    {
        _stt = stt;
        _tts = tts;
        _ttsFallback = ttsFallback;
        _aiService = aiService;
        _toolRegistry = toolRegistry;
        _settingsStore = settingsStore;
        _logger = logger;
        _ambientContext = ambientContext ?? new AmbientContextService();
        _modelPicker = modelPicker;
        _condenser = condenser;
    }

    public VoiceSettings GetSettings() => _settingsStore.Get();

    public void UpdateSettings(VoiceSettings settings) => _settingsStore.Save(settings);

    public async Task ProcessUtteranceAsync(byte[] pcm16, CancellationToken cancellationToken = default)
        => await ProcessUtteranceAsync(pcm16, 16000, cancellationToken);

    public async Task ProcessUtteranceAsync(byte[] pcm16, int sampleRate, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        var settings = _settingsStore.Get();
        string? response = null;

        try
        {
            if (pcm16.Length == 0) return;

            // Barge-in: a new utterance cancels any TTS still playing from the previous one
            var oldTts = Interlocked.Exchange(ref _ttsCts, null);
            oldTts?.Cancel();
            oldTts?.Dispose();

            SetState(VoiceState.Listening);
            _logger.LogInformation("[Voice] Processing utterance ({Bytes} bytes, {Rate} Hz)", pcm16.Length, sampleRate);

            // Le moteur capture à la fréquence native du micro (souvent 44,1/48 kHz),
            // mais Whisper travaille en 16 kHz : on ré-échantillonne avant l'envoi.
            var resampled = sampleRate != 16000 ? Pcm16Resampler.Resample(pcm16, sampleRate, 16000) : pcm16;

            SttResult stt;
            try
            {
                stt = await _stt.TranscribeAsync(
                    resampled, 16000,
                    string.IsNullOrEmpty(settings.SttLanguage) || settings.SttLanguage == "auto" ? null : settings.SttLanguage,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] STT failed");
                State = VoiceState.Error;
                StatusMessage?.Invoke($"Erreur de reconnaissance vocale : {ex.Message}");
                return;
            }

            if (!stt.Success || string.IsNullOrWhiteSpace(stt.Text))
            {
                _logger.LogWarning("[Voice] Empty transcription");
                return;
            }

            _logger.LogInformation("[Voice] Transcription ({Lang}, {Elapsed}ms): \"{Text}\"", stt.Language, stt.ElapsedMs, stt.Text);
            UserTranscript?.Invoke(stt.Text);

            // Confirmation vocale en attente : la réponse prononcée résout la
            // demande de confirmation (oui/non) sans passer par le LLM.
            TaskCompletionSource<VoiceConfirmationAnswer?>? pending = null;
            lock (_confirmationLock)
            {
                if (_pendingConfirmation is not null)
                {
                    pending = _pendingConfirmation;
                    _pendingConfirmation = null;
                }
            }
            if (pending is not null)
            {
                var accepted = IsAffirmative(stt.Text);
                pending.TrySetResult(new VoiceConfirmationAnswer(accepted, stt.Text));
                UtteranceProcessed?.Invoke(new VoiceUtteranceRecord(
                    stt.Text, "", false, accepted ? "confirmé" : "refusé", "confirmation"));
                return;
            }

            var waitingForCommand = DateTime.UtcNow - _lastExchangeUtc < AwaitingCommandTimeout && _lastExchangeUtc != DateTime.MinValue;

            string command = stt.Text;
            var wakeMatched = false;
            var requiresWake = settings.WakeWordEnabled;
            if (requiresWake)
            {
                var extracted = string.Empty;
                wakeMatched = WakeWordMatcher.TryExtractCommand(stt.Text, settings.WakeWordList, out extracted);
                if (wakeMatched) command = extracted;
            }

            // Wake word only -> acknowledge and wait for the command
            if (wakeMatched && string.IsNullOrWhiteSpace(command))
            {
                _logger.LogInformation("[Voice] Wake word detected, awaiting command");
                _lastExchangeUtc = DateTime.UtcNow;
                UtteranceProcessed?.Invoke(new VoiceUtteranceRecord(stt.Text, "", true, null, "info"));
                await PlayTtsAsync("Oui, je vous écoute.", settings, cancellationToken);
                return;
            }

            // Wake word enabled, no wake word and not waiting for a follow-up command -> ignore
            if (requiresWake && !wakeMatched && !waitingForCommand)
            {
                _logger.LogInformation("[Voice] Not a wake word, ignoring");
                return;
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            // Mode passif : Jarvis écoute le contexte (contexte ambiant déjà
            // capturé) mais ne répond pas aux questions.
            if (settings.PassiveMode)
            {
                _logger.LogInformation("[Voice] Passive mode: utterance recorded, no response");
                UtteranceProcessed?.Invoke(new VoiceUtteranceRecord(
                    stt.Text, command, requiresWake && wakeMatched, null, "passif"));
                return;
            }

            // Reset session conversation when idle for a while
            if (DateTime.UtcNow - _lastExchangeUtc > SessionResetTimeout || _sessionConversation is null)
            {
                _sessionConversation = BuildConversation();
            }

            SetState(VoiceState.Processing);
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            response = await GetAiResponseAsync(command, settings.Model, _cts.Token);
            _lastExchangeUtc = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("[Voice] AI returned no response");
                UtteranceProcessed?.Invoke(new VoiceUtteranceRecord(stt.Text, command, requiresWake && wakeMatched, null, "warning"));
                await PlayTtsAsync("Je n'ai pas réussi à obtenir de réponse. Pouvez-vous reformuler ?", settings, cancellationToken);
                return;
            }

            _logger.LogInformation("[Voice] AI response ({Length} chars)", response.Length);
            ResponseGenerated?.Invoke(response);
            UtteranceProcessed?.Invoke(new VoiceUtteranceRecord(stt.Text, command, requiresWake && wakeMatched, response, "success"));

            // Condensation en arrière-plan : si la conversation approche de la limite
            // de tokens, l'historique ancien est résumé par l'IA pendant que la réponse
            // est dite à voix haute, pour que la conversation continue sans tronquer.
            if (_condenser is not null)
            {
                var modelForCondense = settings.Model;
                _ = Task.Run(() => CondenseInBackgroundAsync(modelForCondense));
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Voice] Processing interrupted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Voice] Processing failed");
            SetState(VoiceState.Error);
            StatusMessage?.Invoke($"Erreur vocale : {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _gate.Release();
            if (string.IsNullOrWhiteSpace(response))
            {
                SetState(VoiceState.Idle);
            }
        }

        // TTS is played outside the gate so the next utterance's STT/LLM
        // can already run in parallel while the response is being spoken.
        if (string.IsNullOrWhiteSpace(response)) return;

        CancellationTokenSource? ttsCts = null;
        try
        {
            ttsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _ttsCts, ttsCts);
            SetState(VoiceState.Speaking);
            await PlayTtsAsync(response, settings, ttsCts.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _ttsCts, null, ttsCts!);
            ttsCts?.Dispose();
            SetState(VoiceState.Idle);
        }
    }

    public void Interrupt()
    {
        var processing = _cts;
        var playing = _ttsCts;
        try { processing?.Cancel(); } catch (ObjectDisposedException) { }
        try { playing?.Cancel(); } catch (ObjectDisposedException) { }
        _logger.LogInformation("[Voice] Interrupted by user (barge-in)");
    }

    bool IVoiceConfirmationChannel.IsSupported => true;

    async Task<VoiceConfirmationAnswer?> IVoiceConfirmationChannel.AskAsync(
        string question, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;

        var tcs = new TaskCompletionSource<VoiceConfirmationAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_confirmationLock)
        {
            _pendingConfirmation?.TrySetResult(null);
            _pendingConfirmation = tcs;
        }

        var settings = _settingsStore.Get();

        // Question posée à voix haute (annulable par barge-in comme le reste).
        CancellationTokenSource? ttsCts = null;
        try
        {
            ttsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _ttsCts, ttsCts);
            SetState(VoiceState.Speaking);
            await PlayTtsAsync(question, settings, ttsCts.Token);
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetResult(null);
            return null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _ttsCts, null, ttsCts!);
            ttsCts?.Dispose();
        }

        // On libère le gate pendant l'attente pour que la prochaine locution
        // (« oui » / « non ») puisse être transcrite et résoudre la confirmation.
        _gate.Release();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var answer = await tcs.Task.WaitAsync(timeoutCts.Token);
            SetState(VoiceState.Idle);
            return answer;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Voice] Confirmation vocale : délai d'attente dépassé");
            return null;
        }
        finally
        {
            lock (_confirmationLock)
            {
                if (ReferenceEquals(_pendingConfirmation, tcs)) _pendingConfirmation = null;
            }
            tcs.TrySetResult(null);
            try
            {
                await _gate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static bool IsAffirmative(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.Trim().ToLowerInvariant();
        return AffirmativePhrases.Any(p => lower.Contains(p));
    }

    /// <summary>
    /// Annonce proactive (rappels, notifications, état) : synthèse vocale
    /// best-effort qui n'interrompt pas une conversation en cours et qui
    /// reste annulable par barge-in.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (State != VoiceState.Idle) return;

        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken)) return;

        var settings = _settingsStore.Get();
        CancellationTokenSource? ttsCts = null;
        try
        {
            ttsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _ttsCts, ttsCts);
            SetState(VoiceState.Speaking);
            await PlayTtsAsync(text, settings, ttsCts.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _ttsCts, null, ttsCts!);
            ttsCts?.Dispose();
            _gate.Release();
            SetState(VoiceState.Idle);
        }
    }

    private AIConversation BuildConversation()
    {
        var definitions = new List<AIToolDefinition>();
        foreach (var tool in _toolRegistry.GetAll())
        {
            var properties = new Dictionary<string, AIToolProperty>();
            foreach (var param in tool.Parameters)
            {
                properties[param.Name] = new AIToolProperty(
                    param.Type.Name.ToLowerInvariant(),
                    param.Description);
            }

            definitions.Add(new AIToolDefinition(
                tool.Name,
                tool.Description,
                properties,
                tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToList()));
        }

        var systemPrompt = AgentSystemPrompt.Build(definitions);
        var ambient = _ambientContext.GetContextSummary();
        if (!string.IsNullOrEmpty(ambient))
        {
            systemPrompt += "\n\n[Contexte environnemental perçu autour de l'utilisateur — utilise-le si pertinent]\n"
                            + ambient;
        }

        return new AIConversation(systemPrompt);
    }

    private async Task<string?> GetAiResponseAsync(string command, string model, CancellationToken ct)
    {
        try
        {
            var resolvedModel = string.IsNullOrWhiteSpace(model)
                ? await (_modelPicker?.PickModelAsync(command, ct) ?? Task.FromResult<string?>(null))
                : model;

            // Référence stable : la condensation en arrière-plan peut remplacer
            // _sessionConversation (swap atomique) pendant ce stream.
            var conversation = _sessionConversation;
            var full = new System.Text.StringBuilder();
            await foreach (var token in _aiService.StreamChatAsync(
                command,
                conversation,
                resolvedModel,
                ModelSelectionMode.Auto,
                ct))
            {
                if (!token.Contains(IAIService.StreamRestartMarker))
                    full.Append(token);
            }

            return full.Length == 0 ? null : full.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Voice] AI service error");
            return null;
        }
    }

    private async Task CondenseInBackgroundAsync(string model)
    {
        if (_condenser is null || _sessionConversation is null) return;

        await _condenseGate.WaitAsync();
        try
        {
            var current = _sessionConversation;
            if (current is null) return;

            var condensed = await _condenser.CondenseIfNeededAsync(
                current,
                string.IsNullOrWhiteSpace(model) ? null : model);
            if (condensed is not null)
            {
                _sessionConversation = condensed;
                _logger.LogInformation("[Voice] Conversation condensée en arrière-plan");
                StatusMessage?.Invoke("Contexte de la conversation condensé.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Voice] Condensation en arrière-plan interrompue");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] Condensation en arrière-plan a échoué");
        }
        finally
        {
            _condenseGate.Release();
        }
    }

    private async Task PlayTtsAsync(string text, VoiceSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var clean = TextCleaner.StripMarkdown(text);
        if (clean.Length > MaxTtsChars)
        {
            var cut = clean[..MaxTtsChars];
            var lastDot = cut.LastIndexOf('.');
            clean = lastDot > 300 ? cut[..(lastDot + 1)] : cut;
        }

        // Synthèse phrase par phrase : la première phrase est générée et lancée
        // aussitôt, les suivantes sont synthétisées pendant la lecture en cours,
        // ce qui rend l'utilisateur plus rapidement (et reste annulable par barge-in).
        var sentences = SplitSentences(clean);
        if (sentences.Count <= 1)
        {
            await SynthesizeAndPlayAsync(clean, settings, ct);
            return;
        }

        foreach (var sentence in sentences)
        {
            ct.ThrowIfCancellationRequested();
            await SynthesizeAndPlayAsync(sentence, settings, ct);
        }
    }

    private async Task SynthesizeAndPlayAsync(string text, VoiceSettings settings, CancellationToken ct)
    {
        byte[]? wav = null;
        try
        {
            wav = await _tts.SynthesizeWavAsync(text, settings.TtsVoice, settings.Volume, settings.TtsSpeed, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] Primary TTS ({Tts}) failed, trying fallback", _tts.Name);
            if (_ttsFallback is not null)
            {
                try
                {
                    wav = await _ttsFallback.SynthesizeWavAsync(text, settings.TtsVoice, settings.Volume, settings.TtsSpeed, ct);
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "[Voice] TTS fallback also failed");
                }
            }
        }

        if (wav is { Length: > 0 })
        {
            AudioForPlayback?.Invoke(wav);
        }
        else
        {
            StatusMessage?.Invoke("Impossible de générer la réponse vocale.");
        }
    }

    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            builder.Append(c);
            if (c is '.' or '!' or '?' or ';' or ':' or '\n')
            {
                var sentence = builder.ToString().Trim();
                if (sentence.Length > 0)
                {
                    sentences.Add(sentence);
                    builder.Clear();
                }
            }
        }
        var tail = builder.ToString().Trim();
        if (tail.Length > 0)
            sentences.Add(tail);

        // Évite de couper au milieu d'une abréviation ("M." / "vs.") ou des
        // nombres décimaux : on ne découpe pas les phrases trop courtes.
        return sentences
            .Where(s => s.Length > 2 || s.Length == 0)
            .ToList();
    }
}
