using JarvisAI.Application.Vision;
using JarvisAI.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Génération de vidéos via l'API MiniMax H3 (https://api.minimax.io).
/// Text-to-video, image-to-video, resolution 768P/2K, durée 4-15s.
/// Clé API lue depuis l'env var MINIMAX_API_KEY ou SecureStorage.
/// </summary>
public sealed class MiniMaxVideoService : IVideoGenerationService
{
    private readonly HttpClient _http;
    private readonly ILogger<MiniMaxVideoService> _logger;
    private readonly ISecureStorage? _secureStorage;
    private const string BaseUrl = "https://api.minimax.io";
    private const string Model = "MiniMax-H3";
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "JarvisAI");

    public MiniMaxVideoService(HttpClient httpClient, ILogger<MiniMaxVideoService> logger, ISecureStorage? secureStorage = null)
    {
        _http = httpClient;
        _http.Timeout = TimeSpan.FromMinutes(5);
        _logger = logger;
        _secureStorage = secureStorage;
    }

    public async Task<GeneratedVideo> GenerateVideoAsync(
        string prompt, int durationSeconds = 5, string ratio = "16:9",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new GeneratedVideo(null, null, false, "Un prompt est requis pour générer une vidéo.");

        var apiKey = await GetApiKeyAsync();
        if (string.IsNullOrEmpty(apiKey))
            return new GeneratedVideo(null, null, false,
                "Clé API MiniMax non configurée. " +
                "Définis la variable d'environnement MINIMAX_API_KEY ou configure la clé via le stockage sécurisé.");

        durationSeconds = Math.Clamp(durationSeconds, 4, 15);

        try
        {
            // Étape 1 : Créer la tâche de génération
            var taskId = await CreateTaskAsync(prompt, durationSeconds, ratio, apiKey, cancellationToken);
            if (taskId is null)
                return new GeneratedVideo(null, null, false, "Échec de création de la tâche MiniMax.");

            _logger.LogInformation("[MiniMax] Tâche créée : {TaskId}", taskId);

            // Étape 2 : Polling du statut
            var videoUrl = await PollTaskAsync(taskId, apiKey, cancellationToken);
            if (videoUrl is null)
                return new GeneratedVideo(null, null, false, "La génération vidéo a échoué ou expiré.");

            // Étape 3 : Télécharger la vidéo
            Directory.CreateDirectory(OutputDir);
            var filePath = Path.Combine(OutputDir, $"jarvis-video-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            await DownloadVideoAsync(videoUrl, filePath, cancellationToken);

            _logger.LogInformation("[MiniMax] Vidéo générée : {Path}", filePath);
            return new GeneratedVideo(filePath, videoUrl, true, null);
        }
        catch (OperationCanceledException)
        {
            return new GeneratedVideo(null, null, false, "Génération vidéo annulée.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MiniMax] Échec de génération vidéo");
            return new GeneratedVideo(null, null, false, $"Erreur MiniMax : {ex.Message}");
        }
    }

    private async Task<string?> CreateTaskAsync(
        string prompt, int duration, string ratio, string apiKey, CancellationToken ct)
    {
        var payload = new
        {
            model = Model,
            content = new[]
            {
                new { type = "text", text = prompt }
            },
            duration = duration,
            resolution = "768P",
            ratio = ratio
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/video_generation");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[MiniMax] Create task failed ({Code}): {Body}", (int)response.StatusCode, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("task_id", out var idProp))
            return idProp.GetString();

        _logger.LogWarning("[MiniMax] Pas de task_id dans la réponse: {Body}", body);
        return null;
    }

    private async Task<string?> PollTaskAsync(string taskId, string apiKey, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10_000, ct);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v2/query/video_generation/{taskId}");
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) continue;

                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);

                if (!doc.RootElement.TryGetProperty("task", out var taskProp))
                    continue;

                var status = taskProp.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

                if (status == "succeeded")
                {
                    if (taskProp.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("url", out var urlProp))
                        return urlProp.GetString();
                    return null;
                }

                if (status is "failed" or "cancelled" or "expired")
                {
                    var error = taskProp.TryGetProperty("error", out var err) ? err.GetString() : null;
                    _logger.LogWarning("[MiniMax] Tâche {Status}: {Error}", status, error);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MiniMax] Erreur polling");
            }
        }
        return null;
    }

    private static async Task DownloadVideoAsync(string url, string filePath, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = File.Create(filePath);
        await stream.CopyToAsync(fs, ct);
    }

    private async Task<string?> GetApiKeyAsync()
    {
        // 1. Env var
        var key = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        if (!string.IsNullOrEmpty(key)) return key;

        // 2. SecureStorage
        if (_secureStorage is not null)
        {
            key = await _secureStorage.GetAsync("minimax-api-key");
            if (!string.IsNullOrEmpty(key)) return key;
        }

        return null;
    }
}
