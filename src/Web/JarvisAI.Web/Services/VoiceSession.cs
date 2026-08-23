using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Web.Services;

public sealed class VoiceSession
{
    private readonly VoiceConversationService _voiceService;
    private readonly ILogger<VoiceSession> _logger;
    private readonly ConcurrentDictionary<string, MemoryStream> _buffers = new();
    private readonly ConcurrentDictionary<string, int> _sampleRates = new();

    public VoiceSession(VoiceConversationService voiceService, ILogger<VoiceSession> logger)
    {
        _voiceService = voiceService;
        _logger = logger;
    }

    public void AddChunk(string connectionId, byte[] pcm16, int sampleRate = 0)
    {
        if (sampleRate > 0)
        {
            _sampleRates[connectionId] = sampleRate;
        }
        var stream = _buffers.GetOrAdd(connectionId, _ => new MemoryStream());
        lock (stream)
        {
            stream.Write(pcm16, 0, pcm16.Length);
        }
    }

    public void EndUtterance(string connectionId)
    {
        if (!_buffers.TryRemove(connectionId, out var stream))
        {
            _logger.LogDebug("[Voice] EndUtterance with no buffer");
            return;
        }

        byte[] bytes;
        lock (stream)
        {
            bytes = stream.ToArray();
        }
        stream.Dispose();

        _sampleRates.TryRemove(connectionId, out var rate);
        if (bytes.Length == 0) return;

        var sampleRate = rate > 0 ? rate : 16000;
        _ = Task.Run(() => _voiceService.ProcessUtteranceAsync(bytes, sampleRate));
    }

    public void Reset(string connectionId)
    {
        _sampleRates.TryRemove(connectionId, out _);
        if (_buffers.TryRemove(connectionId, out var stream))
        {
            stream.Dispose();
        }
    }

    public void Interrupt()
    {
        _voiceService.Interrupt();
    }
}
