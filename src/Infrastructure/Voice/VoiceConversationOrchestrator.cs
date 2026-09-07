using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceConversationOrchestrator
{
    Task<VoiceResponse> ProcessUtteranceAsync(string transcribedText, CancellationToken ct = default);
    void SetContext(VoiceContext context);
    VoiceContext GetContext();
    IReadOnlyList<ConversationTurn> GetHistory();
    void ClearHistory();
    Task<string> SummarizeConversationAsync();
    string GetAdaptedResponse(string rawResponse);
    void SetMode(VoiceMode mode);
    VoiceMode GetMode();
    event EventHandler<VoiceEventArgs>? OnStateChanged;
}

public sealed class VoiceConversationOrchestrator : IVoiceConversationOrchestrator, IAsyncDisposable
{
    private readonly ILogger<VoiceConversationOrchestrator> _logger;
    private readonly IEnhancedSttService _stt;
    private readonly IEnhancedTtsService _tts;
    private readonly IEnhancedWakeWordService _wakeWord;
private readonly string _storagePath;
    private VoiceContext _context = new();
    private VoiceMode _mode = VoiceMode.Conversation;
    private readonly object _historyLock = new();
    private readonly List<ConversationTurn> _history = new();
    private readonly ConcurrentQueue<string> _responseQueue = new();

    public event EventHandler<VoiceEventArgs>? OnStateChanged;

    public VoiceConversationOrchestrator(
        ILogger<VoiceConversationOrchestrator> logger,
        IEnhancedSttService stt,
        IEnhancedTtsService tts,
        IEnhancedWakeWordService wakeWord)
    {
        _logger = logger;
        _stt = stt;
        _tts = tts;
        _wakeWord = wakeWord;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "voice_history.json");
        LoadHistory();
    }

    public async Task<VoiceResponse> ProcessUtteranceAsync(string transcribedText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcribedText))
            return new VoiceResponse { Success = false, Error = "Aucune entrée vocale" };

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            OnStateChanged?.Invoke(this, new VoiceEventArgs { State = VoiceProcessingState.Processing });

            // Check for voice commands first
            var commandResult = await ProcessVoiceCommandAsync(transcribedText);
            if (commandResult is not null)
            {
                return commandResult;
            }

            // Add to history
            lock (_historyLock)
            {
                _history.Add(new ConversationTurn
                {
                    Role = "user",
                    Content = transcribedText,
                    Timestamp = DateTime.UtcNow
                });
            }

            // Get adapted response based on mode and sentiment
            var adaptedText = GetAdaptedResponse(transcribedText);

            // Synthesize response
            OnStateChanged?.Invoke(this, new VoiceEventArgs { State = VoiceProcessingState.Speaking });
            var audioData = await _tts.SynthesizeAsync(adaptedText, null, ct);

            sw.Stop();

            // Add response to history
            lock (_historyLock)
            {
                _history.Add(new ConversationTurn
                {
                    Role = "assistant",
                    Content = adaptedText,
                    Timestamp = DateTime.UtcNow,
                    AudioData = audioData
                });
                if (_history.Count > 50)
                    _history.RemoveRange(0, _history.Count - 50);
            }

            _logger.LogInformation("[VoiceConv] Processed in {Ms}ms: {Text}",
                sw.ElapsedMilliseconds, transcribedText[..Math.Min(50, transcribedText.Length)]);

            return new VoiceResponse
            {
                Success = true,
                Text = adaptedText,
                AudioData = audioData,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[VoiceConv] Processing failed");
            return new VoiceResponse { Success = false, Error = ex.Message };
        }
        finally
        {
            OnStateChanged?.Invoke(this, new VoiceEventArgs { State = VoiceProcessingState.Idle });
        }
    }

    public void SetContext(VoiceContext context) => _context = context;

    public VoiceContext GetContext() => _context;

    public IReadOnlyList<ConversationTurn> GetHistory()
    {
        lock (_historyLock) return _history.ToList();
    }

    public void ClearHistory()
    {
        lock (_historyLock) _history.Clear();
        SaveHistory();
    }

    public async Task<string> SummarizeConversationAsync()
    {
        List<ConversationTurn> userMessages;
        lock (_historyLock)
        {
            userMessages = _history.Where(h => h.Role == "user").ToList();
        }
        if (userMessages.Count == 0)
            return "Aucune conversation à résumer.";

        var sb = new StringBuilder();
        sb.AppendLine("Résumé de la conversation:");
        sb.AppendLine();

        foreach (var msg in userMessages.TakeLast(10))
        {
            sb.AppendLine($"- {msg.Content}");
        }

        return sb.ToString();
    }

    public string GetAdaptedResponse(string rawResponse)
    {
        if (_mode == VoiceMode.Command)
        {
            // Short responses for command mode
            if (rawResponse.Length > 100)
                return rawResponse[..100] + "...";
        }

        if (_mode == VoiceMode.Dictation)
        {
            // Return as-is for dictation
            return rawResponse;
        }

        // Conversation mode: adapt to context
        var sentiment = AnalyzeSentiment(rawResponse);
        _tts.SetEmotion(sentiment);

        return rawResponse;
    }

    public void SetMode(VoiceMode mode)
    {
        _mode = mode;
        _logger.LogInformation("[VoiceConv] Mode set to: {Mode}", mode);
    }

    public VoiceMode GetMode() => _mode;

    private async Task<VoiceResponse?> ProcessVoiceCommandAsync(string text)
    {
        var lower = text.ToLowerInvariant().Trim();

        // Mode switching commands
        if (lower.Contains("mode conversation") || lower.Contains("mode normal"))
        {
            SetMode(VoiceMode.Conversation);
            return await CreateCommandResponseAsync("Mode conversation activé");
        }

        if (lower.Contains("mode commande") || lower.Contains("mode silencieux"))
        {
            SetMode(VoiceMode.Command);
            return await CreateCommandResponseAsync("Mode commande activé");
        }

        if (lower.Contains("résume la conversation") || lower.Contains("résumé"))
        {
            var summary = await SummarizeConversationAsync();
            return await CreateCommandResponseAsync(summary);
        }

        if (lower.Contains("efface l'historique") || lower.Contains("nouvelle conversation"))
        {
            ClearHistory();
            return await CreateCommandResponseAsync("Historique effacé");
        }

        if (lower.Contains("arrête") || lower.Contains("stop"))
        {
            return await CreateCommandResponseAsync("D'accord, j'attends.");
        }

        return null;
    }

    private async Task<VoiceResponse> CreateCommandResponseAsync(string text)
    {
        var audio = await _tts.SynthesizeAsync(text);
        return new VoiceResponse
        {
            Success = true,
            Text = text,
            AudioData = audio,
            IsCommand = true
        };
    }

    private string AnalyzeSentiment(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("erreur") || lower.Contains("désolé") || lower.Contains("problème"))
            return "empathetic";

        if (lower.Contains("excellent") || lower.Contains("bravo") || lower.Contains("super"))
            return "excited";

        if (lower.Contains("urgent") || lower.Contains("important") || lower.Contains("immédiatement"))
            return "urgent";

        if (lower.Contains("calmement") || lower.Contains("tranquillement"))
            return "calm";

        return "neutral";
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<ConversationTurn>>(json);
                if (loaded is not null)
                {
                    lock (_historyLock) _history.AddRange(loaded);
                }
            }
        }
        catch { }
    }

    private async Task SaveHistoryAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            List<ConversationTurn> toSave;
            lock (_historyLock)
            {
                toSave = _history.TakeLast(100).ToList();
            }
            var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storagePath, json).ConfigureAwait(false);
        }
        catch { }
    }

    private void SaveHistory()
    {
        SaveHistoryAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await SaveHistoryAsync();
    }
}

public sealed class VoiceResponse
{
    public bool Success { get; set; }
    public string Text { get; set; } = "";
    public byte[] AudioData { get; set; } = Array.Empty<byte>();
    public TimeSpan Duration { get; set; }
    public bool IsCommand { get; set; }
    public string? Error { get; set; }
}

public sealed class VoiceContext
{
    public string? CurrentTask { get; set; }
    public string? UserPreference { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new();
}

public sealed class ConversationTurn
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public byte[]? AudioData { get; set; }
}

public enum VoiceMode
{
    Conversation,
    Command,
    Dictation
}

public enum VoiceProcessingState
{
    Idle,
    Listening,
    Processing,
    Speaking,
    Error
}

public sealed class VoiceEventArgs : EventArgs
{
    public VoiceProcessingState State { get; set; }
    public string? Message { get; set; }
}
