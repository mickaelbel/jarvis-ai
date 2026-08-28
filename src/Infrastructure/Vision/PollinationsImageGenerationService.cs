using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Génération d'images via l'API Pollinations (https://image.pollinations.ai) —
/// 100% gratuite, sans clé API, sans limite pratique pour un usage normal.
/// Renvoie l'image en DataUrl (base64) pour l'afficher/dowloader sans dépendre
/// d'une ressource externe volatile.
/// </summary>
public sealed class PollinationsImageGenerationService : IImageGenerationService
{
    private readonly HttpClient _http;
    private readonly ILogger<PollinationsImageGenerationService> _logger;

    public PollinationsImageGenerationService(HttpClient httpClient, ILogger<PollinationsImageGenerationService> logger)
    {
        _http = httpClient;
        _logger = logger;
    }

    public async Task<GeneratedImage> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new GeneratedImage(null, null, false, "Un prompt est requis pour générer une image.");

        try
        {
            var url = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(prompt)}" +
                      $"?width=1024&height=1024&model=flux&nologo=false&seed={(uint)(DateTime.UtcNow.Ticks % int.MaxValue)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // On lit les octets de l'image pour la servir en DataUrl (self-contained).
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";

            _logger.LogInformation("[ImageGen] Image générée via Pollinations ({PromptLength} chars, {Size} octets)", prompt.Length, bytes.Length);
            return new GeneratedImage(dataUrl, url, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ImageGen] Échec de génération d'image");
            return new GeneratedImage(null, null, false, $"Erreur de génération d'image : {ex.Message}");
        }
    }
}
