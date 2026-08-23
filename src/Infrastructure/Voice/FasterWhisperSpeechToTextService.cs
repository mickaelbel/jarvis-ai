using JarvisAI.Application.Voice;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Voice;

public sealed class FasterWhisperSpeechToTextService : ISpeechToTextService, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FasterWhisperSpeechToTextService> _logger;
    private readonly string? _venvPython;
    private readonly string? _serverScript;
    private readonly bool _autoStart;
    private readonly object _startLock = new();
    private bool _startAttempted;

    public string Name => "faster-whisper (Python)";

    public FasterWhisperSpeechToTextService(
        HttpClient httpClient,
        ILogger<FasterWhisperSpeechToTextService> logger,
        bool autoStart = true)
    {
        _httpClient = httpClient;
        _logger = logger;
        _autoStart = autoStart;
        _venvPython = VoicePaths.FindPythonVenv();
        _serverScript = VoicePaths.FindSttServerScript();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync("/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<SttResult> TranscribeAsync(
        byte[] pcm16, int sampleRate, string? language = null, CancellationToken cancellationToken = default)
    {
        if (!await EnsureStartedAsync(cancellationToken))
        {
            return SttResult.Failed("Le serveur de reconnaissance vocale (faster-whisper) n'est pas disponible.");
        }

        try
        {
            using var content = new ByteArrayContent(pcm16);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Headers.Add("X-Sample-Rate", sampleRate.ToString());

            var response = await _httpClient.PostAsync("/transcribe", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[STT] HTTP {Status}: {Body}", response.StatusCode, body);
                return SttResult.Failed($"STT HTTP {response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                return SttResult.Failed(errorEl.GetString() ?? "Unknown STT error");
            }

            var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? string.Empty : string.Empty;
            var lang = root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String
                ? langEl.GetString()
                : null;
            var elapsed = root.TryGetProperty("elapsed_ms", out var elapsedEl) ? elapsedEl.GetDouble() : 0;

            return SttResult.Ok(text.Trim(), lang, elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STT] Transcription request failed");
            return SttResult.Failed($"STT request failed: {ex.Message}");
        }
    }

    private async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (await IsAvailableAsync(cancellationToken)) return true;
        if (!_autoStart) return false;

        lock (_startLock)
        {
            if (_startAttempted) return false;
            _startAttempted = true;
        }

        if (string.IsNullOrEmpty(_venvPython) || string.IsNullOrEmpty(_serverScript))
        {
            _logger.LogError("[STT] Python venv or stt_server.py not found (venv={Venv}, script={Script})", _venvPython, _serverScript);
            return false;
        }

        _logger.LogInformation("[STT] Starting faster-whisper server: {Python} {Script}", _venvPython, _serverScript);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _venvPython,
                Arguments = $"\"{_serverScript}\"",
                WorkingDirectory = Path.GetDirectoryName(_serverScript),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STT] Failed to start faster-whisper server");
            return false;
        }

        // Wait for the server to come up (model loads lazily, server itself starts fast)
        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAvailableAsync(cancellationToken)) return true;
            await Task.Delay(1000, cancellationToken);
        }

        _logger.LogWarning("[STT] Server did not become ready within 30s");
        return false;
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
