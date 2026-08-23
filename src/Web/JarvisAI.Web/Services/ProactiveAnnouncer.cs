using System.Threading.Channels;
using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Annonces vocales proactives (rappels, notifications) : file FIFO annoncée par
/// le moteur vocal quand Jarvis est au repos et que l'utilisateur ne parle pas.
/// Chaque annonce est best-effort et annulable (barge-in).
/// </summary>
public sealed class ProactiveAnnouncer
{
    private readonly VoiceConversationService _voice;
    private readonly AmbientContextService _ambient;
    private readonly ILogger<ProactiveAnnouncer> _logger;

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _worker = new(1, 1);

    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(3);

    public ProactiveAnnouncer(
        VoiceConversationService voice,
        AmbientContextService ambient,
        ILogger<ProactiveAnnouncer> logger)
    {
        _voice = voice;
        _ambient = ambient;
        _logger = logger;
    }

    public void Enqueue(string message)
    {
        _queue.Writer.TryWrite(message);
        StartWorker();
    }

    private void StartWorker()
    {
        if (!_worker.Wait(0)) return;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync())
            {
                while (_queue.Reader.TryRead(out var message))
                {
                    await WaitForQuietAsync();
                    await _voice.SpeakAsync(message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Proactive] Announcer pump failed");
        }
        finally
        {
            _worker.Release();
        }
    }

    private async Task WaitForQuietAsync()
    {
        while (_voice.State != VoiceState.Idle || _ambient.HasRecentSpeech(QuietWindow))
        {
            await Task.Delay(500);
        }
    }
}
