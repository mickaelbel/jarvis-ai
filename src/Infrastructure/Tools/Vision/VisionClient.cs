using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools.Vision;

/// <summary>
/// Analyse visuelle : capture l'écran et l'envoie à un modèle de vision local
/// (Ollama, ex llava / qwen2.5vl / minicpm-v). Réponse texte, ensuite parlée
/// par Jarvis comme n'importe quel résultat d'outil.
/// </summary>
public static class VisionClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static async Task<string> AnalyserEcranAsync(string question, string modele, CancellationToken ct)
    {
        var chemin = Windows.ScreenCaptureProbe.Capture();
        if (chemin is null || !File.Exists(chemin))
            return "Erreur : capture d'écran impossible.";

        try
        {
            var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(chemin, ct));
            var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") is { Length: > 0 } u
                ? u.TrimEnd('/')
                : "http://127.0.0.1:11434";

            using var contenu = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = modele,
                    prompt = question,
                    images = new[] { base64 },
                    stream = false
                }),
                Encoding.UTF8, "application/json");

            using var reponse = await Http.PostAsync($"{baseUrl}/api/generate", contenu, ct);
            if (!reponse.IsSuccessStatusCode)
                return $"Erreur : le modèle de vision « {modele} » a répondu {reponse.StatusCode}. " +
                       $"Vérifie que Ollama tourne sur {baseUrl} et que le modèle est installé (ollama pull {modele}).";

            await using var flux = await reponse.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(flux, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("response", out var r) && !string.IsNullOrWhiteSpace(r.GetString())
                ? r.GetString()!.Trim()
                : "Je n'ai rien su dire sur cette capture.";
        }
        finally
        {
            try { File.Delete(chemin); } catch { }
        }
    }
}
