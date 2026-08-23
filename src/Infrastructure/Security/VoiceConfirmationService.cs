using JarvisAI.Application.Security;
using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Security;

/// <summary>
/// Confirmation de sécurité des outils à risque : pose la question à voix haute
/// (TTS) et écoute la réponse (STT) quand un canal vocal est disponible
/// (mode Desktop). Sinon, délègue à la confirmation web (modale du navigateur),
/// puis en dernier recours à l'entrée console.
/// </summary>
public sealed class VoiceConfirmationService : IUserConfirmationService
{
    private readonly Lazy<IVoiceConfirmationChannel?>? _voiceChannel;
    private readonly IUserConfirmationService? _fallback;
    private readonly ILogger<VoiceConfirmationService> _logger;
    private readonly string[] _affirmativePhrases;
    private readonly TimeSpan _timeout;

    public VoiceConfirmationService(
        ILogger<VoiceConfirmationService> logger,
        Lazy<IVoiceConfirmationChannel?>? voiceChannel = null,
        IUserConfirmationService? fallback = null,
        SecurityOptions? options = null,
        string[]? affirmativePhrases = null)
    {
        _logger = logger;
        _voiceChannel = voiceChannel;
        _fallback = fallback;
        _affirmativePhrases = affirmativePhrases ??
            new[] { "oui", "yes", "ok", "confirme", "confirmez", "valide", "go", "d'accord", "daccord", "vas-y", "vasy" };
        var seconds = options?.DangerousToolTimeoutSeconds ?? 0;
        _timeout = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(30);
    }

    public async Task<ConfirmationResult> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var channel = _voiceChannel?.Value;
        if (channel is { IsSupported: true })
        {
            var question = BuildQuestion(request);
            try
            {
                var answer = await channel.AskAsync(question, _timeout, cancellationToken);
                sw.Stop();

                if (answer is not null)
                {
                    var confirmed = IsAffirmative(answer.Raw);
                    _logger.LogInformation("[VoiceConfirmation] Réponse vocale : '{Raw}' (Confirmé={Confirmed}, {Elapsed}ms)",
                        answer.Raw, confirmed, sw.ElapsedMilliseconds);
                    return confirmed
                        ? ConfirmationResult.Accepted(ConfirmationMethod.Voice, sw.Elapsed, answer.Raw)
                        : ConfirmationResult.Denied(ConfirmationMethod.Voice, sw.Elapsed, answer.Raw);
                }

                _logger.LogInformation("[VoiceConfirmation] Délai de réponse vocale dépassé; confirmation refusée");
                return ConfirmationResult.Denied(ConfirmationMethod.Voice, sw.Elapsed, "timeout");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return ConfirmationResult.Denied(ConfirmationMethod.Voice, sw.Elapsed, "cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceConfirmation] Échec du canal vocal; bascule sur le fallback");
            }
        }

        if (_fallback is not null)
        {
            return await _fallback.RequestConfirmationAsync(request, cancellationToken);
        }

        return await ConsoleFallbackAsync(request, cancellationToken);
    }

    private static string BuildQuestion(ConfirmationRequest request)
    {
        var action = string.IsNullOrWhiteSpace(request.Description)
            ? $"exécuter l'outil {request.ToolName}"
            : request.Description;
        return $"Voulez-vous que je {action} ? Dites oui pour continuer, ou non pour annuler.";
    }

    private bool IsAffirmative(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var lower = input.Trim().ToLowerInvariant();
        return _affirmativePhrases.Any(phrase => lower.Contains(phrase));
    }

    private async Task<ConfirmationResult> ConsoleFallbackAsync(ConfirmationRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("=== VOICE CONFIRMATION (console) ===");
        Console.WriteLine($"Tool: {request.ToolName} (Risk: {request.RiskLevel})");
        Console.WriteLine("Type 'yes' or 'no':");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var readTask = Task.Run(() => Console.ReadLine()?.Trim() ?? string.Empty, CancellationToken.None);
            var completed = await Task.WhenAny(readTask, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));
            if (completed != readTask)
            {
                timeoutCts.Cancel();
                sw.Stop();
                return ConfirmationResult.Denied(ConfirmationMethod.Voice, sw.Elapsed, "timeout");
            }

            var input = await readTask;
            sw.Stop();
            var confirmed = IsAffirmative(input);
            return confirmed
                ? ConfirmationResult.Accepted(ConfirmationMethod.Voice, sw.Elapsed, input)
                : ConfirmationResult.Denied(ConfirmationMethod.Voice, sw.Elapsed, input);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return ConfirmationResult.Denied(ConfirmationMethod.Voice, sw.Elapsed, "cancelled");
        }
    }
}
