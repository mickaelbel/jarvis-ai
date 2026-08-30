using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IConversationBranchingService
{
    string CreateBranch(string conversationId, string branchName, string? fromMessageId = null);
    IReadOnlyList<ConversationBranch> GetBranches(string conversationId);
    ConversationBranch? GetBranch(string branchId);
    void DeleteBranch(string branchId);
    IReadOnlyList<ChatMessage> GetMessages(string branchId);
    void AddMessage(string branchId, string role, string content);
    void SwitchBranch(string conversationId, string branchId);
    string ExportBranch(string branchId, string format = "markdown");
}

public sealed class ConversationBranchingService : IConversationBranchingService
{
    private readonly ILogger<ConversationBranchingService> _logger;
    private readonly string _storagePath;
    private readonly Dictionary<string, List<ConversationBranch>> _conversations = new();
    private readonly Dictionary<string, List<ChatMessage>> _messages = new();

    public ConversationBranchingService(ILogger<ConversationBranchingService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "conversation_branches.json");
        Load();
    }

    public string CreateBranch(string conversationId, string branchName, string? fromMessageId = null)
    {
        if (!_conversations.ContainsKey(conversationId))
            _conversations[conversationId] = new List<ConversationBranch>();

        var branch = new ConversationBranch
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            ConversationId = conversationId,
            Name = branchName,
            ParentMessageId = fromMessageId,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Copy messages from parent if branching from a specific message
        if (fromMessageId is not null)
        {
            var parentMessages = GetMessageList(conversationId)
                .TakeWhile(m => m.Id != fromMessageId)
                .Concat(new[] { GetMessageList(conversationId).FirstOrDefault(m => m.Id == fromMessageId) })
                .Where(m => m is not null)
                .ToList();

            _messages[branch.Id] = parentMessages!;
        }
        else
        {
            _messages[branch.Id] = new List<ChatMessage>();
        }

        _conversations[conversationId].Add(branch);
        Save();

        _logger.LogInformation("[Branch] Created: {Name} in {Conversation}", branchName, conversationId);
        return branch.Id;
    }

    public IReadOnlyList<ConversationBranch> GetBranches(string conversationId)
    {
        return _conversations.TryGetValue(conversationId, out var branches)
            ? branches.OrderByDescending(b => b.CreatedAt).ToList()
            : new List<ConversationBranch>();
    }

    public ConversationBranch? GetBranch(string branchId)
    {
        return _conversations.Values
            .SelectMany(b => b)
            .FirstOrDefault(b => b.Id == branchId);
    }

    public void DeleteBranch(string branchId)
    {
        foreach (var conv in _conversations)
        {
            conv.Value.RemoveAll(b => b.Id == branchId);
        }
        _messages.Remove(branchId);
        Save();
    }

    public IReadOnlyList<ChatMessage> GetMessages(string branchId)
    {
        return _messages.TryGetValue(branchId, out var messages)
            ? messages
            : new List<ChatMessage>();
    }

    public void AddMessage(string branchId, string role, string content)
    {
        if (!_messages.ContainsKey(branchId))
            _messages[branchId] = new List<ChatMessage>();

        _messages[branchId].Add(new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Role = role,
            Content = content,
            Timestamp = DateTime.UtcNow
        });

        Save();
    }

    public void SwitchBranch(string conversationId, string branchId)
    {
        if (_conversations.TryGetValue(conversationId, out var branches))
        {
            foreach (var branch in branches)
                branch.IsActive = branch.Id == branchId;
            Save();
        }
    }

    public string ExportBranch(string branchId, string format = "markdown")
    {
        var messages = GetMessages(branchId);
        var branch = GetBranch(branchId);

        if (branch is null) return "Branche non trouvée";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Conversation: {branch.Name}");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            var role = msg.Role == "user" ? "Utilisateur" : "Jarvis";
            sb.AppendLine($"## {role}");
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private List<ChatMessage> GetMessageList(string conversationId)
    {
        return _messages.Values
            .FirstOrDefault(m => m.Any()) ?? new List<ChatMessage>();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("conversations", out var convsEl))
                {
                    foreach (var convProp in convsEl.EnumerateObject())
                    {
                        var loaded = JsonSerializer.Deserialize<List<ConversationBranch>>(convProp.Value.GetRawText());
                        if (loaded is not null)
                            _conversations[convProp.Name] = loaded;
                    }
                }

                if (root.TryGetProperty("messages", out var msgsEl))
                {
                    foreach (var msgProp in msgsEl.EnumerateObject())
                    {
                        var loaded = JsonSerializer.Deserialize<List<ChatMessage>>(msgProp.Value.GetRawText());
                        if (loaded is not null)
                            _messages[msgProp.Name] = loaded;
                    }
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

            var data = new { conversations = _conversations, messages = _messages };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class ConversationBranch
{
    public string Id { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentMessageId { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ChatMessage
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
