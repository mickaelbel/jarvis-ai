using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Groups voice utterances into titled conversation sessions. A new session is
/// started after the same idle timeout the voice engine uses to reset its
/// conversation (5 minutes) so both stay in sync.
/// </summary>
public sealed class VoiceSessionTracker : IDisposable
{
    private readonly ConversationSessionService _sessions;
    private readonly JarvisAI.Application.Voice.VoiceConversationService _voiceService;
    private readonly ILogger<VoiceSessionTracker> _logger;

    private string? _currentSessionId;
    private DateTime _lastUtteranceUtc = DateTime.MinValue;
    private string _pendingTranscript = "";

    private static readonly TimeSpan SessionResetTimeout = TimeSpan.FromMinutes(5);

    public VoiceSessionTracker(
        ConversationSessionService sessions,
        JarvisAI.Application.Voice.VoiceConversationService voiceService,
        ILogger<VoiceSessionTracker> logger)
    {
        _sessions = sessions;
        _voiceService = voiceService;
        _logger = logger;
        _voiceService.UserTranscript += OnUserTranscript;
        _voiceService.UtteranceProcessed += OnUtteranceProcessed;
        _voiceService.StatusMessage += OnStatusMessage;
    }

    private void OnUserTranscript(string text) => _pendingTranscript = text;

    private void OnUtteranceProcessed(VoiceUtteranceRecord record)
    {
        var now = DateTime.UtcNow;
        if (_currentSessionId is null || now - _lastUtteranceUtc > SessionResetTimeout)
        {
            _currentSessionId = _sessions.CreateSession("voice").Id;
            _logger.LogInformation("[Voice] New voice session {Id}", _currentSessionId);
        }
        _lastUtteranceUtc = now;

        if (!string.IsNullOrWhiteSpace(record.Transcript))
            _sessions.AppendMessage(_currentSessionId!, "user", record.Transcript);

        if (!string.IsNullOrWhiteSpace(record.Response))
            _sessions.AppendMessage(_currentSessionId!, "assistant", record.Response);

        _ = _sessions.EnsureTitleAsync(_currentSessionId!, record.Transcript);
        _ = _sessions.CheckRetitleAsync(_currentSessionId!);
        _pendingTranscript = "";
    }

    private void OnStatusMessage(string message)
    {
        if (!message.StartsWith("Erreur", StringComparison.OrdinalIgnoreCase)) return;
        if (_currentSessionId is null) return;

        if (!string.IsNullOrWhiteSpace(_pendingTranscript))
        {
            _sessions.AppendMessage(_currentSessionId, "user", _pendingTranscript);
            _pendingTranscript = "";
        }
        _sessions.AppendMessage(_currentSessionId, "assistant", message);
    }

    public void Dispose()
    {
        _voiceService.UserTranscript -= OnUserTranscript;
        _voiceService.UtteranceProcessed -= OnUtteranceProcessed;
        _voiceService.StatusMessage -= OnStatusMessage;
    }
}
