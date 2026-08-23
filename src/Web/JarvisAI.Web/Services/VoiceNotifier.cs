using JarvisAI.Application.Voice;
using JarvisAI.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public sealed class VoiceNotifier : IDisposable
{
    private readonly VoiceConversationService _voiceService;
    private readonly IHubContext<VoiceHub> _hubContext;
    private readonly VoiceConnectionRegistry _registry;
    private readonly AppVoiceStatus _status;
    private readonly ILogger<VoiceNotifier> _logger;
    private Timer? _statusTimer;

    public VoiceNotifier(
        VoiceConversationService voiceService,
        IHubContext<VoiceHub> hubContext,
        VoiceConnectionRegistry registry,
        AppVoiceStatus status,
        ILogger<VoiceNotifier> logger)
    {
        _voiceService = voiceService;
        _hubContext = hubContext;
        _registry = registry;
        _status = status;
        _logger = logger;

        _voiceService.StateChanged += OnStateChanged;
        _voiceService.UserTranscript += OnUserTranscript;
        _voiceService.AudioForPlayback += OnAudioForPlayback;
        _voiceService.StatusMessage += OnStatusMessage;

        _statusTimer = new Timer(_ => PushStatusPanel(), null, 1000, 2000);
    }

    private async void OnStateChanged(VoiceState state)
    {
        try
        {
            await PushAsync("voiceState", state.ToString());
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[VoiceNotifier] push failed"); }
    }

    private async void OnUserTranscript(string text)
    {
        try
        {
            await PushAsync("voiceTranscript", text);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[VoiceNotifier] push failed"); }
    }

    private async void OnAudioForPlayback(byte[] wav)
    {
        try
        {
            await PushAsync("voiceAudio", Convert.ToBase64String(wav));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[VoiceNotifier] push failed"); }
    }

    private async void OnStatusMessage(string message)
    {
        try
        {
            await PushAsync("voiceStatus", message);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[VoiceNotifier] push failed"); }
    }

    private static readonly JsonSerializerOptions PanelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async void PushStatusPanel()
    {
        try
        {
            var json = JsonSerializer.Serialize(_status.Snapshot(), PanelJson);
            await PushAsync("voiceStatusPanel", json);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[VoiceNotifier] status push failed"); }
    }

    private async Task PushAsync(string method, string payload)
    {
        var clients = _registry.All;
        if (clients.Count == 0) return;

        var connectionIds = clients.ToArray();
        foreach (var id in connectionIds)
        {
            try
            {
                await _hubContext.Clients.Client(id).SendAsync(method, payload);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VoiceNotifier] Failed to push to {Id}", id);
            }
        }
    }

    public void Dispose()
    {
        _statusTimer?.Dispose();
        _statusTimer = null;
        _voiceService.StateChanged -= OnStateChanged;
        _voiceService.UserTranscript -= OnUserTranscript;
        _voiceService.AudioForPlayback -= OnAudioForPlayback;
        _voiceService.StatusMessage -= OnStatusMessage;
    }
}
