using JarvisAI.Application.Voice;
using JarvisAI.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Hubs;

public sealed class VoiceHub : Hub
{
    private readonly VoiceSession _session;
    private readonly VoiceConnectionRegistry _registry;
    private readonly ILogger<VoiceHub> _logger;

    public VoiceHub(VoiceSession session, VoiceConnectionRegistry registry, ILogger<VoiceHub> logger)
    {
        _session = session;
        _registry = registry;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _registry.Add(Context.ConnectionId);
        _logger.LogInformation("[VoiceHub] Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Remove(Context.ConnectionId);
        _session.Reset(Context.ConnectionId);
        _logger.LogInformation("[VoiceHub] Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task SendAudioChunk(string base64Pcm16, int sampleRate = 0)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Pcm16);
            _session.AddChunk(Context.ConnectionId, bytes, sampleRate);
        }
        catch (FormatException)
        {
            _logger.LogWarning("[VoiceHub] Invalid base64 chunk from {ConnectionId}", Context.ConnectionId);
        }
        return Task.CompletedTask;
    }

    public Task EndUtterance()
    {
        _session.EndUtterance(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public Task ResetUtterance()
    {
        _session.Reset(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public Task Interrupt()
    {
        _logger.LogInformation("[VoiceHub] Barge-in from {ConnectionId}", Context.ConnectionId);
        _session.Interrupt();
        return Task.CompletedTask;
    }
}
