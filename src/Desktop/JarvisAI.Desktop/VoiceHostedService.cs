using JarvisAI.Application.Voice;
using JarvisAI.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JarvisAI.Desktop;

/// <summary>
/// Hosted Service "toujours actif" du moteur vocal Desktop : capture du micro,
/// VAD, STT (Whisper), LLM et TTS (Piper) tournent dans le processus
/// JarvisAI.Desktop.exe, indépendamment de l'interface web (Chrome fermé,
/// page changée, fenêtre réduite, verrouillage Windows…).
/// Le navigateur ne fait qu'AFFICHER l'état via SignalR.
/// </summary>
public sealed class VoiceHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private BackgroundVoiceEngine? _engine;

    /// <summary>Moteur vocal courant (accès global pour le push-to-talk).</summary>
    public static BackgroundVoiceEngine? Engine { get; private set; }

    public VoiceHostedService(IServiceProvider services)
    {
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var voice = _services.GetRequiredService<VoiceConversationService>();
            var settings = _services.GetRequiredService<IVoiceSettingsStore>();
            var status = _services.GetRequiredService<AppVoiceStatus>();
            var ambient = _services.GetRequiredService<AmbientContextService>();
            var wakeWord = _services.GetService<IWakeWordDetector>();
            _engine = new BackgroundVoiceEngine(voice, settings, status, ambient, wakeWord);
            Engine = _engine;
            await _engine.StartAsync();
            App.Log("[VoiceHostedService] Engine started (always-on)");
        }
        catch (Exception ex)
        {
            App.Log("[VoiceHostedService] Start failed: " + ex);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal de l'hôte.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _engine?.Dispose();
            App.Log("[VoiceHostedService] Engine stopped");
        }
        catch (Exception ex)
        {
            App.Log("[VoiceHostedService] Stop error: " + ex);
        }
        await base.StopAsync(cancellationToken);
    }
}
