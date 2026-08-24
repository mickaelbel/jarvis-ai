using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Voice;
using JarvisAI.Domain.Events.Agents;
using JarvisAI.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public sealed class VoiceNotifier : IDisposable
{
    private readonly VoiceConversationService _voiceService;
    private readonly IHubContext<VoiceHub> _hubContext;
    private readonly IHubContext<OverlayHub> _overlayHub;
    private readonly VoiceConnectionRegistry _registry;
    private readonly AppVoiceStatus _status;
    private readonly ILogger<VoiceNotifier> _logger;
    private Timer? _statusTimer;
    private readonly IDisposable _toolSub;

    public VoiceNotifier(
        VoiceConversationService voiceService,
        IHubContext<VoiceHub> hubContext,
        IHubContext<OverlayHub> overlayHub,
        VoiceConnectionRegistry registry,
        AppVoiceStatus status,
        IEventBus eventBus,
        ILogger<VoiceNotifier> logger)
    {
        _voiceService = voiceService;
        _hubContext = hubContext;
        _overlayHub = overlayHub;
        _registry = registry;
        _status = status;
        _logger = logger;

        _voiceService.StateChanged += OnStateChanged;
        _voiceService.UserTranscript += OnUserTranscript;
        _voiceService.AudioForPlayback += OnAudioForPlayback;
        _voiceService.StatusMessage += OnStatusMessage;
        _voiceService.PartialResponse += OnPartialResponse;
        _voiceService.UserTranscriptPartial += OnUserPartial;

        // HUD : chaque outil exécuté s'allume en direct dans l'overlay.
        _toolSub = eventBus.Subscribe<AgentToolExecutedEvent>(async (evt, ct) =>
        {
            try
            {
                await _overlayHub.Clients.Group("desktop").SendAsync("overlayTool", new
                {
                    name = evt.ToolName,
                    success = evt.Success,
                    ms = (int)evt.Duration.TotalMilliseconds
                }, ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[VoiceNotifier] overlayTool push failed"); }
        });

        _statusTimer = new Timer(_ => PushStatusPanel(), null, 1000, 2000);
        // Waveform HUD : niveau micro poussé 8x/s au groupe desktop.
        _waveTimer = new Timer(async _ =>
        {
            try
            {
                await _overlayHub.Clients.Group("desktop").SendAsync("overlayLevel", Math.Round(_status.LastRms, 3));
            }
            catch { /* best-effort */ }
        }, null, 400, 125);
    }

    private readonly Timer? _waveTimer;

    private async void OnStateChanged(VoiceState state)
    {
        try
        {
            await PushAsync("voiceState", state.ToString());
            // HUD desktop : état vocal temps réel (écoute / réflexion / parole).
            await _overlayHub.Clients.Group("desktop").SendAsync("overlayState", state.ToString());
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[VoiceNotifier] push failed"); }
    }

    private async void OnPartialResponse(string text)
    {
        try
        {
            // HUD desktop : texte qui s'écrit en direct pendant le stream.
            await _overlayHub.Clients.Group("desktop").SendAsync("overlayPartial", text);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[VoiceNotifier] partial push failed"); }
    }

    private async void OnUserPartial(string text)
    {
        try
        {
            // HUD : ce que l'utilisateur est en train de dire, en direct.
            await _overlayHub.Clients.Group("desktop").SendAsync("overlayUser", text);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "[VoiceNotifier] user partial push failed"); }
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
        _voiceService.PartialResponse -= OnPartialResponse;
    }
}
