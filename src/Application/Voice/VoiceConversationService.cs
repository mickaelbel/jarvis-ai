using JarvisAI.Application.AI;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Threading.Channels;

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
    private readonly Memory.IEpisodicMemoryService? _episodicMemory;
    private readonly ILogger<VoiceConversationService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _condenseGate = new(1, 1);
    private readonly object _confirmationLock = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _ttsCts;
    private AIConversation? _sessionConversation;
    private DateTime _lastExchangeUtc = DateTime.MinValue;
    private DateTime _reveilSeulUtc = DateTime.MinValue;
    private TaskCompletionSource<VoiceConfirmationAnswer?>? _pendingConfirmation;

    private static readonly TimeSpan SessionResetTimeout = TimeSpan.FromMinutes(5);
    // Après un échange COMPLET, courte fenêtre pour une relance sans mot-clé.
    private static readonly TimeSpan AwaitingCommandTimeout = TimeSpan.FromSeconds(25);
    // Après un réveil SEUL (« Jarvis. » → « Oui je vous écoute »), Jarvis
    // attend longtemps que l'utilisateur formule sa commande.
    private static readonly TimeSpan ReveilSeulTimeout = TimeSpan.FromSeconds(200);
    private const int MaxTtsChars = 2600;

    private static readonly string[] AffirmativePhrases =
    {
        "oui", "yes", "ok", "d'accord", "daccord", "vas-y", "vasy", "confirme", "confirmez",
        "valide", "go", "exact", "c'est bon", "cest bon", "bien sûr", "bien sur", "yep", "ouaip", "s'il vous plaît"
    };

    public event Action<VoiceState>? StateChanged;
    public event Action<string>? UserTranscript;
    public event Action<string>? ResponseGenerated;
    public event Action<string>? PartialResponse;
    public event Action<byte[]>? AudioForPlayback;
    public event Action<string>? StatusMessage;
    /// <summary>Transcription intermédiaire pendant que l'utilisateur parle encore (STT partiel).</summary>
    public event Action<string>? UserTranscriptPartial;

    /// <summary>Appelé par le moteur desktop : transcription partielle du flux micro.</summary>
    public void RaiseUserTranscriptPartial(string texte)
        => UserTranscriptPartial?.Invoke(texte);
    public event Action<VoiceUtteranceRecord>? UtteranceProcessed;

    public VoiceState State { get; private set; } = VoiceState.Idle;

    /// <summary>Densité de parole ambiante (~30 s), alimentée par le moteur
    /// desktop : sert à ignorer les réveils provenant d'une vidéo/TV.</summary>
    public double AmbientSpeechDensity { get; set; }

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
        ConversationCondenser? condenser = null,
        Memory.IEpisodicMemoryService? episodicMemory = null)
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
        _episodicMemory = episodicMemory;
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
        CancellationTokenSource? speakCts = null;
        Task speakTask = Task.CompletedTask;

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

            // Anti-hallucination : Whisper invente des phrases types sur du
            // bruit/silence (« sous-titres… », « thank you »…) qui faisaient
            // parler Jarvis sans raison. On les jette silencieusement.
            if (EstHallucinationProbable(stt.Text))
            {
                _logger.LogInformation("[Voice] Transcription rejetée (hallucination probable) : \"{Text}\"", stt.Text);
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

            var now = DateTime.UtcNow;
            var waitingForCommand =
                (_reveilSeulUtc != DateTime.MinValue && now - _reveilSeulUtc < ReveilSeulTimeout) ||
                (_lastExchangeUtc != DateTime.MinValue && now - _lastExchangeUtc < AwaitingCommandTimeout);

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
                // « Jarvis » isolé alors qu'une voix environnante parle sans
                // arrêt (vidéo YouTube, TV…) : presque toujours un mot de la
                // vidéo, pas l'utilisateur. On n'ouvre PAS la fenêtre d'écoute.
                if (AmbientSpeechDensity > 0.55)
                {
                    _logger.LogInformation("[Voice] Réveil isolé ignoré (voix ambiante dense {D:P0})", AmbientSpeechDensity);
                    return;
                }
                _logger.LogInformation("[Voice] Wake word detected, awaiting command");
                _reveilSeulUtc = DateTime.UtcNow;
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

            // Pipeline streaming : la synthèse démarre dès la première phrase
            // produite par le LLM (latence perçue divisée par ~5). Le token de
            // parole est enregistré dans _ttsCts dès maintenant pour qu'un
            // barge-in annule à la fois le flux IA restant et les phrases
            // pas encore synthétisées.
            speakCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _ttsCts, speakCts);

            var resolvedModel = string.IsNullOrWhiteSpace(settings.Model)
                ? await (_modelPicker?.PickModelAsync(command, _cts.Token) ?? Task.FromResult<string?>(null))
                : settings.Model;

            var (streamedResponse, streamedSpeakTask) = await StreamAndSpeakAsync(
                command, resolvedModel, settings, speakCts.Token);
            response = streamedResponse;
            speakTask = streamedSpeakTask;
            _lastExchangeUtc = DateTime.UtcNow;
            _reveilSeulUtc = DateTime.MinValue; // la commande a été servie : fin de la fenêtre longue

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("[Voice] AI returned no response");
                UtteranceProcessed?.Invoke(new VoiceUtteranceRecord(stt.Text, command, requiresWake && wakeMatched, null, "warning"));
                Interlocked.CompareExchange(ref _ttsCts, null, speakCts);
                speakCts.Dispose();
                speakCts = null;
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
            response = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Voice] Processing failed");
            SetState(VoiceState.Error);
            StatusMessage?.Invoke($"Erreur vocale : {ex.Message}");
            response = null;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            // Chemin d'exception : coupe le pipeline de parole orphelin.
            if (string.IsNullOrWhiteSpace(response) && speakCts is not null)
            {
                try { speakCts.Cancel(); } catch (ObjectDisposedException) { }
                Interlocked.CompareExchange(ref _ttsCts, null, speakCts);
                try { speakCts.Dispose(); } catch (ObjectDisposedException) { }
                speakCts = null;
            }
            _gate.Release();
            if (string.IsNullOrWhiteSpace(response))
            {
                SetState(VoiceState.Idle);
            }
        }

        // La parole continue hors du gate : le STT/LLM de la prochaine
        // locution peut déjà tourner pendant que la réponse est dite.
        // speakTask synthétise/joue les phrases au fil de l'eau depuis que le
        // LLM produit ses tokens ; ici on se contente d'attendre la fin.
        if (string.IsNullOrWhiteSpace(response)) return;

        try
        {
            SetState(VoiceState.Speaking);
            await speakTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Voice] Lecture interrompue");
        }
        finally
        {
            if (speakCts is not null)
            {
                Interlocked.CompareExchange(ref _ttsCts, null, speakCts);
                try { speakCts.Dispose(); } catch (ObjectDisposedException) { }
                speakCts = null;
            }
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

    /// <summary>Phrases types que faster-whisper produit sur du bruit/silence.</summary>
    private static readonly string[] HallucinationPatterns =
    {
        "sous-titre", "merci d'avoir regardé", "merci d'avoir suivi", "merci davoir regardé",
        "abonne-toi", "abonnez-vous", "abonne toi", "thank you for watching", "thanks for watching",
        "stay tuned", "see you next time", "à bientôt sur", "a bientôt sur", "bye bye",
        "sous titres réalisés", "sous-titres réalisés", "au nom de la communauté"
    };

    public static bool EstHallucinationProbable(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return true;
        // Uniquement de la ponctuation / des points de suspension (« ... »).
        if (trimmed.All(c => !char.IsLetter(c))) return true;
        var lower = trimmed.ToLowerInvariant();
        foreach (var pattern in HallucinationPatterns)
        {
            if (lower.Contains(pattern)) return true;
        }
        return false;
    }

    private AIConversation BuildConversation()
    {        var definitions = new List<AIToolDefinition>();
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

    /// <summary>
    /// Pipeline streaming : les phrases produites par le LLM sont extraites au
    /// fil des tokens, mises dans un canal, et synthétisées en parallèle — la
    /// phrase N+1 est générée pendant que la phrase N est lue. Retourne le
    /// texte complet (pour l'historique/événements) et la tâche de parole,
    /// à attendre hors du gate.
    /// </summary>
    private async Task<(string? Response, Task SpeakTask)> StreamAndSpeakAsync(
        string command, string? model, VoiceSettings settings, CancellationToken ct)
    {
        // Référence stable : la condensation en arrière-plan peut remplacer
        // _sessionConversation (swap atomique) pendant ce stream.
        var conversation = _sessionConversation;

        // Mémoire épisodique : rappel des échanges passés pertinents, injecté
        // silencieusement avant la commande (n'affecte pas la synthèse vocale).
        var llmCommand = command;
        if (_episodicMemory is not null)
        {
            try
            {
                var recall = await _episodicMemory.RecallBlockAsync(command, ct);
                if (!string.IsNullOrEmpty(recall))
                    llmCommand = recall + "\n\n" + command;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* le rappel ne doit jamais bloquer */ }
        }

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var full = new StringBuilder();
        var pending = new StringBuilder();
        var failed = false;
        var spokenChars = 0;
        long lastPartialMs = 0;
        int lastPartialLen = 0;

        // Producteur : flux LLM -> découpe en phrases -> canal.
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var token in _aiService.StreamChatAsync(
                    llmCommand, conversation, model, ModelSelectionMode.Auto, ct))
                {
                    if (token.Contains(IAIService.StreamRestartMarker)) continue;
                    full.Append(token);
                    pending.Append(token);

                    // HUD live : aperçu throttlé (~5 Hz) du texte en cours.
                    var now = Environment.TickCount64;
                    if (now - lastPartialMs > 200 && full.Length - lastPartialLen >= 12)
                    {
                        lastPartialMs = now;
                        lastPartialLen = full.Length;
                        PartialResponse?.Invoke(full.ToString());
                    }

                    if (spokenChars >= MaxTtsChars) continue;
                    foreach (var sentence in DrainSentences(pending, flush: false))
                    {
                        channel.Writer.TryWrite(sentence);
                        spokenChars += sentence.Length;
                    }
                }
                if (spokenChars < MaxTtsChars)
                {
                    foreach (var sentence in DrainSentences(pending, flush: true))
                        channel.Writer.TryWrite(sentence);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                failed = true;
                _logger.LogWarning(ex, "[Voice] Flux IA interrompu en cours de réponse");
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await producer;

        if (failed && full.Length == 0)
            return (null, Task.CompletedTask);

        // Souvenir épisodique de l'échange (feu et oubli : jamais bloquant).
        if (_episodicMemory is not null && full.Length > 0)
        {
            var episodeQuestion = command;
            var episodeAnswer = full.ToString();
            _ = Task.Run(() => _episodicMemory.RecordAsync(episodeQuestion, episodeAnswer, "voix"),
                CancellationToken.None);
        }

        // Consommateur : chevauchement synthèse/lecture dans l'ordre. La
        // synthèse de la phrase suivante démarre dès que celle de la courante
        // est finie ; la lecture (côté Desktop) enchaîne les WAV reçus.
        var speakTask = Task.Run(async () =>
        {
            Task<byte[]?>? inFlight = null;
            try
            {
                await foreach (var sentence in channel.Reader.ReadAllAsync(ct))
                {
                    var thisSynth = TrySynthesizeAsync(sentence, settings, ct);
                    if (inFlight is null)
                    {
                        inFlight = thisSynth;
                        continue;
                    }
                    EmitAudio(await inFlight);
                    inFlight = thisSynth;
                }
                if (inFlight is not null)
                    EmitAudio(await inFlight);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Voice] Pipeline de lecture interrompu");
            }
        }, CancellationToken.None);

        return (full.Length == 0 ? null : full.ToString(), speakTask);
    }

    private void EmitAudio(byte[]? wav)
    {
        if (wav is { Length: > 0 })
            AudioForPlayback?.Invoke(wav);
        else
            StatusMessage?.Invoke("Impossible de générer la réponse vocale.");
    }

    /// <summary>
    /// Nettoie le markdown/formatage que le TTS lirait littéralement
    /// (« astérisque astérisque » pour le gras, URL épelées, etc.).
    /// </summary>
    internal static string CleanForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Liens markdown [texte](url) -> texte ; URLs nues -> retirées.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"https?://\S+", string.Empty);
        // Balisage markdown : gras/italique (* _), titres (#), code (` ~).
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[*_#`~]+", string.Empty);
        // Espaces résiduels.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Trim();
    }

    private async Task<byte[]?> TrySynthesizeAsync(string text, VoiceSettings settings, CancellationToken ct)
    {
        text = CleanForSpeech(text);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return await _tts.SynthesizeWavAsync(text, settings.TtsVoice, settings.Volume, settings.TtsSpeed, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] Primary TTS ({Tts}) failed, trying fallback", _tts.Name);
            if (_ttsFallback is not null)
            {
                try
                {
                    return await _ttsFallback.SynthesizeWavAsync(text, settings.TtsVoice, settings.Volume, settings.TtsSpeed, ct);
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "[Voice] TTS fallback also failed");
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Extraction incrémentale : consomme du buffer les phrases terminées
    /// (ponctuation finale trouvée), en protégeant les décimales ("3.14")
    /// et les abréviations trop courtes ("M.", "vs."). Avec flush=true,
    /// rend aussi le fragment restant (fin du flux).
    /// </summary>
    internal static List<string> DrainSentences(StringBuilder pending, bool flush)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < pending.Length; i++)
        {
            var c = pending[i];
            if (c is not ('.' or '!' or '?' or ';' or ':' or '\n')) continue;

            // Décimale "3.14" : point entre deux chiffres -> pas une fin.
            if (c == '.' && i > start && char.IsDigit(pending[i - 1]))
            {
                if (i + 1 >= pending.Length) break;      // chiffre suivant pas encore arrivé
                if (char.IsDigit(pending[i + 1])) continue;
            }

            // Initiale "M. Dupont" : point après un mot d'une seule lettre -> pas une fin.
            if (c == '.' && i - 2 >= start && !char.IsWhiteSpace(pending[i - 1]) && char.IsWhiteSpace(pending[i - 2]))
                continue;

            var segment = pending.ToString(start, i - start + 1).Trim();
            if (segment.Length > 2 || (flush && segment.Length > 0))
                result.Add(segment);
            start = i + 1;
        }

        if (start > 0)
        {
            pending.Remove(0, start);
            while (pending.Length > 0 && char.IsWhiteSpace(pending[0]))
                pending.Remove(0, 1);
        }

        if (flush && pending.Length > 0)
        {
            var tail = pending.ToString().Trim();
            pending.Clear();
            if (tail.Length > 0)
                result.Add(tail);
        }
        return result;
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
        EmitAudio(await TrySynthesizeAsync(text, settings, ct));
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
