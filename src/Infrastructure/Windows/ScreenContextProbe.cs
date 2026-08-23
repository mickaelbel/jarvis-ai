using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace JarvisAI.Infrastructure.Windows;

/// <summary>
/// OCR natif Windows (Windows.Media.Ocr) du contenu de l'écran principal,
/// pour le contexte des souvenirs épisodiques (« qu'avais-je à l'écran… »).
/// </summary>
public static class ScreenContextProbe
{
    private static readonly object Lock = new();
    private static string? _cacheTexte;
    private static DateTime _cacheAt = DateTime.MinValue;
    private const int CacheSecondes = 45;

    /// <summary>Texte visible à l'écran (cache 45 s), ou null si indisponible.</summary>
    public static string? GetRecentOcr(int maxChars = 280)
    {
        lock (Lock)
        {
            if ((DateTime.UtcNow - _cacheAt).TotalSeconds < CacheSecondes)
                return _cacheTexte;
        }

        var texte = CaptureOcr(maxChars);
        lock (Lock)
        {
            _cacheTexte = texte;
            _cacheAt = DateTime.UtcNow;
        }
        return texte;
    }

    /// <summary>Capture + reconnaissance immédiate, sans cache.</summary>
    public static string? CaptureOcr(int maxChars = 600)
    {
        try
        {
            var pngPath = ScreenCaptureProbe.Capture();
            if (pngPath is null || !File.Exists(pngPath)) return null;
            var bytes = File.ReadAllBytes(pngPath);
            try { File.Delete(pngPath); } catch { /* temporaire */ }

            var brut = OcrAsync(bytes).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(brut)) return null;

            var texte = Regex.Replace(brut, @"\s+", " ").Trim();
            return texte.Length <= maxChars ? texte : texte[..maxChars];
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> OcrAsync(byte[] png)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;

        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var writer = new DataWriter(stream);
            writer.WriteBytes(png);
            await writer.StoreAsync().AsTask();
            await writer.FlushAsync().AsTask();
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var bitmap = await decoder.GetSoftwareBitmapAsync();
            try
            {
                var resultat = await engine.RecognizeAsync(bitmap).AsTask();
                return resultat.Text;
            }
            finally { bitmap.Dispose(); }
        }
        finally { stream.Dispose(); }
    }
}
