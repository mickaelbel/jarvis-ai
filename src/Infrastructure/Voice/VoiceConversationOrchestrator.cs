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
            var commandResult = ProcessVoiceCommand(transcribedText);
            if (commandResult is not null)
            {
                return commandResult;
            }

            // Add to history
            _history.Add(new ConversationTurn
            {
                Role = "user",
                Content = transcribedText,
                Timestamp = DateTime.UtcNow
            });

            // Keep history manageable
            if (_history.Count > 50)
                _history.RemoveRange(0, _history.Count - 50);

            // Get adapted response based on mode and sentiment
            var adaptedText = GetAdaptedResponse(transcribedText);

            // Synthesize response
            OnStateChanged?.Invoke(this, new VoiceEventArgs { State = VoiceProcessingState.Speaking });
            var audioData = await _tts.SynthesizeAsync(adaptedText, null, ct);

            sw.Stop();

            // Add response to history
            _history.Add(new ConversationTurn
            {
                Role = "assistant",
                Content = adaptedText,
                Timestamp = DateTime.UtcNow,
                AudioData = audioData
            });

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

    public IReadOnlyList<ConversationTurn> GetHistory() => _history.AsReadOnly();

    public void ClearHistory()
    {
        _history.Clear();
        SaveHistory();
    }

    public async Task<string> SummarizeConversationAsync()
    {
        if (_history.Count == 0)
            return "Aucune conversation à résumer.";

        var sb = new StringBuilder();
        sb.AppendLine("Résumé de la conversation:");
        sb.AppendLine();

        var userMessages = _history.Where(h => h.Role == "user").ToList();
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

    private VoiceResponse? ProcessVoiceCommand(string text)
    {
        var lower = text.ToLowerInvariant().Trim();

        // Mode switching commands
        if (lower.Contains("mode conversation") || lower.Contains("mode normal"))
        {
            SetMode(VoiceMode.Conversation);
            return CreateCommandResponse("Mode conversation activé");
        }

        if (lower.Contains("mode commande") || lower.Contains("mode silencieux"))
        {
            SetMode(VoiceMode.Command);
            return CreateCommandResponse("Mode commande activé");
        }

        if (lower.Contains("résume la conversation") || lower.Contains("résumé"))
        {
            var summary = SummarizeConversationAsync().Result;
            return CreateCommandResponse(summary);
        }

        if (lower.Contains("efface l'historique") || lower.Contains("nouvelle conversation"))
        {
            ClearHistory();
            return CreateCommandResponse("Historique effacé");
        }

        if (lower.Contains("arrête") || lower.Contains("stop"))
        {
            return CreateCommandResponse("D'accord, j'attends.");
        }

        return null;
    }

    private VoiceResponse CreateCommandResponse(string text)
    {
        var audio = _tts.SynthesizeAsync(text).Result;
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
                if (loaded is not null) _history.AddRange(loaded);
            }
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Keep only last 100 turns
            var toSave = _history.TakeLast(100).ToList();
            var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        SaveHistory();
        await ValueTask.CompletedTask;
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
