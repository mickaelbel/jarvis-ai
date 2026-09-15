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

    // Un écran 4K en PNG → base64 peut peser plusieurs Mo, inutiles pour un
    // modèle de vision qui redimensionne déjà en interne. On borne la capture :
    // plus petit payload → latence et temps d'inférence bien réduits.
    private const int MaxLongueurCote = 1600;

    public static async Task<string> AnalyserEcranAsync(string question, string modele, CancellationToken ct)
    {
        var chemin = Windows.ScreenCaptureProbe.Capture();
        if (chemin is null || !File.Exists(chemin))
            return "Erreur : capture d'écran impossible.";

        try
        {
            var base64 = Convert.ToBase64String(await RedimensionnerPourVisionAsync(chemin, ct));
            var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") is { Length: > 0 } u
                ? u.TrimEnd('/')
                : "http://127.0.0.1:11434";

            var reponse = await GenererAsync(baseUrl, modele, base64, question, ct);
            if (!reponse.Ok && reponse.ModeleAbsent)
            {
                // Modèle de vision manquant → on le télécharge puis on réessaie une fois.
                var pull = await TelechargerModeleAsync(baseUrl, modele, ct);
                if (!pull) return reponse.Message;
                reponse = await GenererAsync(baseUrl, modele, base64, question, ct);
            }
            return reponse.Message;
        }
        finally
        {
            try { File.Delete(chemin); } catch { }
        }
    }

    private sealed class ReponseVision
    {
        public bool Ok { get; init; }
        public bool ModeleAbsent { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    private static async Task<ReponseVision> GenererAsync(string baseUrl, string modele, string base64, string question, CancellationToken ct)
    {
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
        if (reponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ReponseVision
            {
                Ok = false,
                ModeleAbsent = true,
                Message = $"Erreur : le modèle de vision « {modele} » n'est pas installé."
            };
        }
        if (!reponse.IsSuccessStatusCode)
        {
            return new ReponseVision
            {
                Ok = false,
                Message = $"Erreur : le modèle de vision « {modele} » a répondu {reponse.StatusCode}. " +
                          $"Vérifie que Ollama tourne sur {baseUrl} et que le modèle est installé (ollama pull {modele})."
            };
        }

        await using var flux = await reponse.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(flux, cancellationToken: ct);
        var texte = doc.RootElement.TryGetProperty("response", out var r) && !string.IsNullOrWhiteSpace(r.GetString())
            ? r.GetString()!.Trim()
            : "Je n'ai rien su dire sur cette capture.";
        return new ReponseVision { Ok = true, Message = texte };
    }

    /// <summary>
    /// Télécharge un modèle de vision via /api/pull en lisant le flux de statuts
    /// (gros volumes : on laisse le timeout long et on loggue la progression).
    /// </summary>
    private static async Task<bool> TelechargerModeleAsync(string baseUrl, string modele, CancellationToken ct)
    {
        using var pull = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        using var contenu = new StringContent(
            JsonSerializer.Serialize(new { name = modele, stream = true }),
            Encoding.UTF8, "application/json");

        try
        {
            using var reponse = await pull.PostAsync($"{baseUrl}/api/pull", contenu, ct);
            if (!reponse.IsSuccessStatusCode) return false;

            string? dernierStatut = null;
            await using var flux = await reponse.Content.ReadAsStreamAsync(ct);
            using var lecteur = new StreamReader(flux);
            while (await lecteur.ReadLineAsync(ct) is { } ligne)
            {
                if (string.IsNullOrWhiteSpace(ligne)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(ligne);
                    if (doc.RootElement.TryGetProperty("error", out _)) return false;
                    dernierStatut = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                }
                catch { }
            }
            return string.Equals(dernierStatut, "success", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Redimensionne la capture à au plus <see cref="MaxLongueurCote"/> px sur le
    /// plus grand côté (proportionnel), puis la ré-encode en PNG. Retourne les
    /// octets prêts pour le base64. Aucun artefact de fichier laissé derrière.
    /// </summary>
    private static async Task<byte[]> RedimensionnerPourVisionAsync(string chemin, CancellationToken ct)
    {
        using var bmp = new System.Drawing.Bitmap(chemin);
        var maxCote = Math.Max(bmp.Width, bmp.Height);

        // Petite capture (< borne) : inutile de redimensionner, on renvoie tel quel.
        if (maxCote <= MaxLongueurCote)
            return await File.ReadAllBytesAsync(chemin, ct);

        var facteur = (double)MaxLongueurCote / maxCote;
        var largeur = Math.Max(1, (int)Math.Round(bmp.Width * facteur));
        var hauteur = Math.Max(1, (int)Math.Round(bmp.Height * facteur));

        using var reduit = new System.Drawing.Bitmap(largeur, hauteur);
        using (var g = System.Drawing.Graphics.FromImage(reduit))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(bmp, 0, 0, largeur, hauteur);
        }

        using var mem = new MemoryStream();
        reduit.Save(mem, System.Drawing.Imaging.ImageFormat.Png);
        return mem.ToArray();
    }
}
