using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface ICollaborationService
{
    Task<string> CreateSessionAsync(string hostName, CancellationToken ct = default);
    Task<CollaborationSession?> GetSessionAsync(string sessionId, CancellationToken ct = default);
    Task<bool> JoinSessionAsync(string sessionId, string guestName, CancellationToken ct = default);
    Task LeaveSessionAsync(string sessionId, string participantId, CancellationToken ct = default);
    Task SendMessageAsync(string sessionId, string participantId, string message, CancellationToken ct = default);
    Task<IReadOnlyList<CollaborationMessage>> GetMessagesAsync(string sessionId, CancellationToken ct = default);
    Task ShareScreenAsync(string sessionId, string participantId, string screenData, CancellationToken ct = default);
}

public sealed class CollaborationService : ICollaborationService
{
    private readonly ILogger<CollaborationService> _logger;
    private readonly List<CollaborationSession> _sessions = new();

    public CollaborationService(ILogger<CollaborationService> logger)
    {
        _logger = logger;
    }

    public async Task<string> CreateSessionAsync(string hostName, CancellationToken ct = default)
    {
        var session = new CollaborationSession
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            HostName = hostName,
            CreatedAt = DateTime.UtcNow,
            IsLive = true
        };

        session.Participants.Add(new CollaborationParticipant
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = hostName,
            IsHost = true,
            JoinedAt = DateTime.UtcNow
        });

        _sessions.Add(session);
        _logger.LogInformation("[Collab] Session created by {Name}: {Id}", hostName, session.Id);
        return session.Id;
    }

    public async Task<CollaborationSession?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        return await Task.FromResult(_sessions.FirstOrDefault(s => s.Id == sessionId));
    }

    public async Task<bool> JoinSessionAsync(string sessionId, string guestName, CancellationToken ct = default)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId && s.IsLive);
        if (session is null) return false;

        session.Participants.Add(new CollaborationParticipant
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = guestName,
            IsHost = false,
            JoinedAt = DateTime.UtcNow
        });

        _logger.LogInformation("[Collab] {Name} joined session {Id}", guestName, sessionId);
        return true;
    }

    public async Task LeaveSessionAsync(string sessionId, string participantId, CancellationToken ct = default)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        session?.Participants.RemoveAll(p => p.Id == participantId);
        await Task.CompletedTask;
    }

    public async Task SendMessageAsync(string sessionId, string participantId, string message, CancellationToken ct = default)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;

        var participant = session.Participants.FirstOrDefault(p => p.Id == participantId);
        session.Messages.Add(new CollaborationMessage
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            ParticipantId = participantId,
            ParticipantName = participant?.Name ?? "Unknown",
            Content = message,
            Timestamp = DateTime.UtcNow
        });

        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CollaborationMessage>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        return await Task.FromResult(session?.Messages.ToList() ?? new List<CollaborationMessage>());
    }

    public async Task ShareScreenAsync(string sessionId, string participantId, string screenData, CancellationToken ct = default)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;

        session.Messages.Add(new CollaborationMessage
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            ParticipantId = participantId,
            Content = "[Screen Share]",
            Type = MessageType.ScreenShare,
            Timestamp = DateTime.UtcNow,
            Metadata = screenData
        });

        await Task.CompletedTask;
    }
}

public sealed class CollaborationSession
{
    public string Id { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsLive { get; set; }
    public List<CollaborationParticipant> Participants { get; set; } = new();
    public List<CollaborationMessage> Messages { get; set; } = new();
}

public sealed class CollaborationParticipant
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsHost { get; set; }
    public DateTime JoinedAt { get; set; }
}

public sealed class CollaborationMessage
{
    public string Id { get; set; } = "";
    public string ParticipantId { get; set; } = "";
    public string ParticipantName { get; set; } = "";
    public string Content { get; set; } = "";
    public MessageType Type { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Metadata { get; set; }
}

public enum MessageType { Text, System, ScreenShare }
