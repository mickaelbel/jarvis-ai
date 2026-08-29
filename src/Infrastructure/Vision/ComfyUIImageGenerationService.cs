using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Vision;

/// <summary>
/// Génération d'images via une instance locale de Stable Diffusion (ComfyUI,
/// API HTTP sur http://127.0.0.1:8188). 100% gratuit, illimité et privé, la
/// meilleure qualité quand la machine a un GPU capable. Utilisé en priorité
/// par le service adaptatif, qui bascule sur Pollinations si ComfyUI est
/// indisponible (machine sans GPU ou serveur non démarré).
/// </summary>
public sealed class ComfyUIImageGenerationService : IImageGenerationService
{
    private readonly HttpClient _http;
    private readonly ILogger<ComfyUIImageGenerationService> _logger;
    private const string Base = "http://127.0.0.1:8188";

    public ComfyUIImageGenerationService(HttpClient httpClient, ILogger<ComfyUIImageGenerationService> logger)
    {
        _http = httpClient;
        _http.Timeout = TimeSpan.FromMinutes(4);
        _logger = logger;
    }

    public async Task<GeneratedImage> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            var ckpt = await GetFirstCheckpointAsync(cancellationToken);
            if (ckpt is null)
            {
                _logger.LogWarning("[ComfyUI] Aucun checkpoint dispo (serveur local absent ou sans modèle)");
                return new GeneratedImage(null, null, false, "ComfyUI indisponible ou sans modèle");
            }

            var workflow = BuildWorkflow(prompt, ckpt);
            var payload = new { prompt = workflow };
            using var postContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var postResp = await _http.PostAsync($"{Base}/prompt", postContent, cancellationToken);
            if (!postResp.IsSuccessStatusCode)
                return new GeneratedImage(null, null, false, $"ComfyUI /prompt a répondu {(int)postResp.StatusCode}");

            using var postJson = JsonDocument.Parse(await postResp.Content.ReadAsStringAsync(cancellationToken));
            var promptId = postJson.RootElement.GetProperty("prompt_id").GetString();
            if (string.IsNullOrEmpty(promptId))
                return new GeneratedImage(null, null, false, "ComfyUI n'a pas renvoyé de prompt_id");

            return await WaitForImageAsync(promptId, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GeneratedImage(null, null, false, "Délai dépassé pour la génération locale");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ComfyUI] Échec de génération locale");
            return new GeneratedImage(null, null, false, $"ComfyUI indisponible : {ex.Message}");
        }
    }

    private async Task<GeneratedImage> WaitForImageAsync(string promptId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var histResp = await _http.GetAsync($"{Base}/history/{promptId}", cancellationToken);
            if (histResp.IsSuccessStatusCode)
            {
                var json = await histResp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(promptId, out var entry) &&
                    entry.TryGetProperty("outputs", out var outputs))
                {
                    foreach (var (_, outNode) in EnumerateProperties(outputs))
                    {
                        if (outNode.TryGetProperty("images", out var imgs)
                            && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
                        {
                            var img = imgs[0];
                            var filename = img.GetProperty("filename").GetString();
                            var subfolder = img.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "";
                            var fileUrl = $"{Base}/view?filename={Uri.EscapeDataString(filename!)}&subfolder={Uri.EscapeDataString(subfolder)}&type=output";
                            using var fileResp = await _http.GetAsync(fileUrl, cancellationToken);
                            if (!fileResp.IsSuccessStatusCode)
                                return new GeneratedImage(null, null, false, "Impossible de récupérer l'image locale");
                            var bytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
                            var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
                            _logger.LogInformation("[ComfyUI] Image générée localement ({PromptLength} chars, {Size} octets)", promptId.Length > 40 ? 40 : promptId.Length, bytes.Length);
                            return new GeneratedImage(dataUrl, fileUrl, true, null);
                        }
                    }
                }
            }
            await Task.Delay(1000, cancellationToken);
        }
        return new GeneratedImage(null, null, false, "Délai dépassé pour la génération locale");
    }

    private async Task<string?> GetFirstCheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await _http.GetAsync($"{Base}/object_info/CheckpointLoaderSimple", cancellationToken);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("CheckpointLoaderSimple", out var loader))
                return null;
            if (loader.TryGetProperty("input", out var input) && input.TryGetProperty("required", out var req)
                && req.TryGetProperty("ckpt_name", out var names) && names.ValueKind == JsonValueKind.Array
                && names.GetArrayLength() > 0)
            {
                return names[0].GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static object BuildWorkflow(string prompt, string ckpt)
    {
        var seed = (uint)(DateTime.UtcNow.Ticks % int.MaxValue);
        return new
        {
            w4 = new { class_type = "CheckpointLoaderSimple", _meta = new { title = "Chargeur de modèle" }, inputs = new { ckpt_name = ckpt } },
            w6 = new { class_type = "CLIPTextEncode", _meta = new { title = "Prompt positif" }, inputs = new { text = prompt, clip = new object[] { "w4", 1 } } },
            w7 = new { class_type = "CLIPTextEncode", _meta = new { title = "Prompt négatif" }, inputs = new { text = "blurry, low quality, distorted, extra limbs, deformed, watermark, text", clip = new object[] { "w4", 2 } } },
            w3 = new { class_type = "KSampler", _meta = new { title = "Échantillonneur" }, inputs = new { seed = seed, steps = 28, cfg = 7.0, sampler_name = "euler", scheduler = "normal", denoise = 1.0, model = new object[] { "w4", 0 }, positive = new object[] { "w6", 0 }, negative = new object[] { "w7", 0 }, latent_image = new object[] { "w5", 0 } } },
            w5 = new { class_type = "EmptyLatentImage", _meta = new { title = "Taille" }, inputs = new { width = 1024, height = 1024, batch_size = 1 } },
            w8 = new { class_type = "VAEDecode", _meta = new { title = "Décodage VAE" }, inputs = new { samples = new object[] { "w3", 0 }, vae = new object[] { "w4", 2 } } },
            w9 = new { class_type = "SaveImage", _meta = new { title = "Sauvegarde" }, inputs = new { filename_prefix = "jarvis", images = new object[] { "w8", 0 } } },
        };
    }

    private static IEnumerable<(string Key, JsonElement Value)> EnumerateProperties(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
            yield return (prop.Name, prop.Value);
    }
}
