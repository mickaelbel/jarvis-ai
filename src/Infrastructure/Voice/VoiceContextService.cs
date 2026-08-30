using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public interface IVoiceContextService
{
    Task<VoiceContextData> GetContextAsync(string sessionId);
    Task UpdateContextAsync(string sessionId, VoiceContextData context);
    Task<List<ConversationTopic>> GetTopicsAsync(string sessionId);
    Task AddTopicAsync(string sessionId, ConversationTopic topic);
    Task<string> GetAdaptedGreetingAsync(string timeOfDay);
    Task<string> GetSentimentResponseAsync(string sentiment);
    Task<VoiceContextData> AnalyzeAndAdaptAsync(string utterance, VoiceContextData currentContext);
}

public sealed class VoiceContextService : IVoiceContextService
{
    private readonly ILogger<VoiceContextService> _logger;
    private readonly string _storagePath;
    private readonly Dictionary<string, VoiceContextData> _sessions = new();

    public VoiceContextService(ILogger<VoiceContextService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JarvisAI", "voice_context.json");
        Load();
    }

    public Task<VoiceContextData> GetContextAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var context))
        {
            context = new VoiceContextData { SessionId = sessionId };
            _sessions[sessionId] = context;
        }
        return Task.FromResult(context);
    }

    public Task UpdateContextAsync(string sessionId, VoiceContextData context)
    {
        _sessions[sessionId] = context;
        Save();
        return Task.CompletedTask;
    }

    public Task<List<ConversationTopic>> GetTopicsAsync(string sessionId)
    {
        var context = _sessions.GetValueOrDefault(sessionId);
        return Task.FromResult(context?.Topics ?? new List<ConversationTopic>());
    }

    public Task AddTopicAsync(string sessionId, ConversationTopic topic)
    {
        if (!_sessions.ContainsKey(sessionId))
            _sessions[sessionId] = new VoiceContextData { SessionId = sessionId };

        _sessions[sessionId].Topics.Add(topic);
        Save();
        return Task.CompletedTask;
    }

    public Task<string> GetAdaptedGreetingAsync(string timeOfDay)
    {
        var greeting = timeOfDay.ToLowerInvariant() switch
        {
            "morning" or "matin" => "Bonjour ! Comment puis-je vous aider aujourd'hui ?",
            "afternoon" or "après-midi" => "Bon après-midi ! Que puis-je faire pour vous ?",
            "evening" or "soir" => "Bonsoir ! Comment puis-je vous être utile ?",
            "night" or "nuit" => "Bonne nuit ! Vous travaillez tard. Puis-je vous aider ?",
            _ => "Bonjour ! Je suis Jarvis, votre assistant. Comment puis-je vous aider ?"
        };
        return Task.FromResult(greeting);
    }

    public Task<string> GetSentimentResponseAsync(string sentiment)
    {
        var response = sentiment.ToLowerInvariant() switch
        {
            "positive" or "heureux" or "content" => "Je suis content de vous entendre de bonne humeur !",
            "negative" or "triste" or "frustré" => "Je comprends que ce n'est pas facile. Je suis là pour vous aider.",
            "angry" or "énervé" or "furieux" => "Je vais faire de mon mieux pour résoudre ce problème rapidement.",
            "confused" or "perdu" => "Pas de souci, je vais vous expliquer plus clairement.",
            "urgent" or "pressé" => "Je vais être bref. Voici ce que je peux faire :",
            _ => ""
        };
        return Task.FromResult(response);
    }

    public Task<VoiceContextData> AnalyzeAndAdaptAsync(string utterance, VoiceContextData currentContext)
    {
        var lower = utterance.ToLowerInvariant();

        // Detect topic
        if (lower.Contains("météo") || lower.Contains("temps"))
        {
            currentContext.CurrentTopic = "weather";
            currentContext.LastInteractionType = "question";
        }
        else if (lower.Contains("musique") || lower.Contains("chanson"))
        {
            currentContext.CurrentTopic = "music";
            currentContext.LastInteractionType = "command";
        }
        else if (lower.Contains("aide") || lower.Contains("comment"))
        {
            currentContext.CurrentTopic = "help";
            currentContext.LastInteractionType = "question";
        }
        else if (lower.Contains("screenshot") || lower.Contains("capture"))
        {
            currentContext.CurrentTopic = "screenshot";
            currentContext.LastInteractionType = "command";
        }

        // Track interaction
        currentContext.InteractionCount++;
        currentContext.LastInteraction = DateTime.UtcNow;
        currentContext.RecentUtterances.Add(utterance);
        if (currentContext.RecentUtterances.Count > 10)
            currentContext.RecentUtterances.RemoveAt(0);

        return Task.FromResult(currentContext);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, VoiceContextData>>(json);
                if (data is not null)
                {
                    foreach (var kv in data)
                        _sessions[kv.Key] = kv.Value;
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class VoiceContextData
{
    public string SessionId { get; set; } = "";
    public string? CurrentTopic { get; set; }
    public string LastInteractionType { get; set; } = "";
    public int InteractionCount { get; set; }
    public DateTime? LastInteraction { get; set; }
    public List<string> RecentUtterances { get; set; } = new();
    public List<ConversationTopic> Topics { get; set; } = new();
    public Dictionary<string, object> Variables { get; set; } = new();
}

public sealed class ConversationTopic
{
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int MessageCount { get; set; }
}
