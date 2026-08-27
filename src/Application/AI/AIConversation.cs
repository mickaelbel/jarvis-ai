namespace JarvisAI.Application.AI;

public sealed class AIConversation
{
    private readonly List<AIMessage> _messages = new();
    private readonly string _systemPrompt;

    public IReadOnlyList<AIMessage> Messages => _messages.AsReadOnly();
    public string SystemPrompt => _systemPrompt;

    public AIConversation(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
    }

    public void AddUserMessage(string content)
    {
        _messages.Add(AIMessage.User(content));
    }

    public void AddAssistantMessage(string content)
    {
        _messages.Add(AIMessage.Assistant(content));
    }

    public void AddAssistantWithToolCalls(string content, IReadOnlyList<AIToolCall> toolCalls)
    {
        _messages.Add(AIMessage.AssistantWithToolCalls(content, toolCalls));
    }

    public void AddToolResult(string toolCallId, string toolCallName, string result)
    {
        _messages.Add(AIMessage.Tool(result, toolCallId, toolCallName));
    }

    public void AddMessage(AIMessage message)
    {
        _messages.Add(message);
    }

    public int IndexOfLastUserMessage()
    {
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            if (_messages[i].Role == AIMessageRole.User)
                return i;
        }
        return -1;
    }

    public string? LastUserText()
    {
        var index = IndexOfLastUserMessage();
        return index >= 0 ? _messages[index].Content : null;
    }

    public void ReplaceLastUserMessage(string content)
    {
        var index = IndexOfLastUserMessage();
        if (index < 0)
        {
            AddUserMessage(content);
            return;
        }
        _messages[index] = AIMessage.User(content);
    }

    public void RemoveFrom(int index)
    {
        if (index < 0 || index >= _messages.Count) return;
        _messages.RemoveRange(index, _messages.Count - index);
    }

    public IReadOnlyList<AIMessage> ToRequestMessages()
    {
        return _messages.ToList().AsReadOnly();
    }

    public void Clear()
    {
        _messages.Clear();
    }

    /// <summary>Copie profonde : même prompt système et mêmes messages.
    /// Utilisé pour donner à chaque génération son propre historique (barge-in :
    /// une nouvelle génération démarre d'un snapshot sans corrompre celle en cours).</summary>
    public AIConversation Clone()
    {
        var copy = new AIConversation(_systemPrompt);
        foreach (var m in _messages)
            copy.AddMessage(m);
        return copy;
    }

    public void Trim(int maxMessages)
    {
        if (_messages.Count > maxMessages)
        {
            _messages.RemoveRange(0, _messages.Count - maxMessages);
        }
    }
}
