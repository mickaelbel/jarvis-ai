using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Automation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

public sealed class MediaAutomationTool : ToolBase
{
    private readonly IMediaAutomationService _mediaService;
    private readonly IVideoMontageService _montageService;
    private readonly ISlideshowService _slideshowService;
    private readonly IFileConversionService _conversionService;
    private readonly ILogger<MediaAutomationTool> _logger;

    public override string Name => "media_automation";
    public override string Description => "Automatisation multimédia : montage, diaporama, redimensionnement, vignettes, filigrane, extraction audio, GIF, normalisation, sous-titres, conversion. Usage: media_automation(action: \"montage\", clips: \"C:/a.mp4,C:/b.mp4\", output_path: \"C:/montage.mp4\"). Actions : montage, slideshow, resize, thumbnails, watermark, extract_audio, gif, normalize, subtitles, convert.";
    public override string Category => "multimedia";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public override TimeSpan Timeout => TimeSpan.FromMinutes(30);
    public override string? WaitingPhrase => "Je monte les médias, un instant…";

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "montage, slideshow, resize, thumbnails, watermark, extract_audio, gif, normalize, subtitles, convert (requis)", typeof(string), required: true),
        new ToolParameter("clips", "Liste de clips vidéo séparées par des virgules (montage)", typeof(string)),
        new ToolParameter("images", "Liste d'images séparées par des virgules (slideshow)", typeof(string)),
        new ToolParameter("image", "Chemin d'une image à convertir (convert)", typeof(string)),
        new ToolParameter("video_path", "Chemin de la vidéo source", typeof(string)),
        new ToolParameter("music_path", "Chemin de la musique de fond", typeof(string)),
        new ToolParameter("folder", "Dossier contenant les médias", typeof(string)),
        new ToolParameter("output_path", "Chemin du fichier de sortie", typeof(string)),
        new ToolParameter("width", "Largeur cible (resize, défaut 1920)", typeof(string)),
        new ToolParameter("height", "Hauteur cible (resize, défaut 1080)", typeof(string)),
        new ToolParameter("interval_seconds", "Intervalle des vignettes (défaut 30)", typeof(string)),
        new ToolParameter("watermark_path", "Chemin du filigrane (watermark)", typeof(string)),
        new ToolParameter("start_seconds", "Position de début en secondes (gif)", typeof(string)),
        new ToolParameter("duration_seconds", "Durée en secondes (gif, défaut 5)", typeof(string)),
        new ToolParameter("language", "Langue des sous-titres (défaut fr)", typeof(string)),
        new ToolParameter("source_format", "Format de sortie extraction audio ou conversion d'image", typeof(string)),
        new ToolParameter("transition_seconds", "Durée de transition en secondes (montage/slideshow, défaut 1)", typeof(string)),
        new ToolParameter("slide_duration_seconds", "Durée de chaque plan (montage/slideshow, défaut 8 ou 4)", typeof(string))
    };

    public MediaAutomationTool(
        IMediaAutomationService mediaService,
        IVideoMontageService montageService,
        ISlideshowService slideshowService,
        IFileConversionService conversionService,
        ILogger<MediaAutomationTool> logger)
        : base(logger)
    {
        _mediaService = mediaService;
        _montageService = montageService;
        _slideshowService = slideshowService;
        _conversionService = conversionService;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var action = RequireParam(parameters, "action").ToLowerInvariant();
        parameters.TryGetValue("clips", out var clips);
        parameters.TryGetValue("images", out var images);
        parameters.TryGetValue("image", out var image);
        parameters.TryGetValue("video_path", out var videoPath);
        parameters.TryGetValue("music_path", out var musicPath);
        parameters.TryGetValue("folder", out var folder);
        parameters.TryGetValue("output_path", out var outputPath);
        parameters.TryGetValue("width", out var widthStr);
        parameters.TryGetValue("height", out var heightStr);
        parameters.TryGetValue("interval_seconds", out var intervalStr);
        parameters.TryGetValue("watermark_path", out var watermarkPath);
        parameters.TryGetValue("start_seconds", out var startStr);
        parameters.TryGetValue("duration_seconds", out var durationStr);
        parameters.TryGetValue("language", out var language);
        parameters.TryGetValue("source_format", out var sourceFormat);
        parameters.TryGetValue("transition_seconds", out var transitionStr);
        parameters.TryGetValue("slide_duration_seconds", out var slideDurationStr);

        var width = 1920;
        if (int.TryParse(widthStr, out var parsedWidth) && parsedWidth > 0)
            width = parsedWidth;
        var height = 1080;
        if (int.TryParse(heightStr, out var parsedHeight) && parsedHeight > 0)
            height = parsedHeight;
        var intervalSeconds = 30;
        if (int.TryParse(intervalStr, out var parsedInterval) && parsedInterval > 0)
            intervalSeconds = parsedInterval;
        var startSeconds = 0;
        if (int.TryParse(startStr, out var parsedStart) && parsedStart >= 0)
            startSeconds = parsedStart;
        var durationSeconds = 5;
        if (int.TryParse(durationStr, out var parsedDuration) && parsedDuration > 0)
            durationSeconds = parsedDuration;
        var transitionSeconds = 1.0;
        if (double.TryParse(transitionStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedTransition) && parsedTransition >= 0)
            transitionSeconds = parsedTransition;
        var slideDurationSeconds = 8;
        if (int.TryParse(slideDurationStr, out var parsedSlide) && parsedSlide > 0)
            slideDurationSeconds = parsedSlide;

        return action switch
        {
            "montage" => await MontageAsync(clips, outputPath, musicPath, transitionSeconds, slideDurationSeconds, ct),
            "slideshow" => await SlideshowAsync(images, outputPath, musicPath, transitionSeconds, slideDurationSeconds, ct),
            "resize" => await ResizeAsync(folder, width, height, ct),
            "thumbnails" => await ThumbnailsAsync(videoPath, folder, intervalSeconds, ct),
            "watermark" => await WatermarkAsync(folder, watermarkPath, ct),
            "extract_audio" => await ExtractAudioAsync(videoPath, sourceFormat, ct),
            "gif" => await GifAsync(videoPath, startSeconds, durationSeconds, ct),
            "normalize" => await NormalizeAsync(folder, ct),
            "subtitles" => await SubtitlesAsync(videoPath, language, ct),
            "convert" => await ConvertAsync(image, sourceFormat, ct),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : montage, slideshow, resize, thumbnails, watermark, extract_audio, gif, normalize, subtitles, convert")
        };
    }

    private async Task<ToolResult> MontageAsync(string? clipsStr, string? outputPath, string? musicPath, double transitionSeconds, int clipDurationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clipsStr))
            return Fail("Le paramètre 'clips' est requis.");
        if (string.IsNullOrWhiteSpace(outputPath))
            return Fail("Le paramètre 'output_path' est requis.");

        var clips = clipsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var result = await _montageService.CreateMontageAsync(clips, outputPath, musicPath, transitionSeconds, clipDurationSeconds, ct);
        return FormatMediaResult("Montage", result);
    }

    private async Task<ToolResult> SlideshowAsync(string? imagesStr, string? outputPath, string? musicPath, double transitionSeconds, int slideDurationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imagesStr))
            return Fail("Le paramètre 'images' est requis.");
        if (string.IsNullOrWhiteSpace(outputPath))
            return Fail("Le paramètre 'output_path' est requis.");

        var images = imagesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var result = await _slideshowService.CreateSlideshowAsync(images, outputPath, musicPath, transitionSeconds, slideDurationSeconds, ct);
        return FormatMediaResult("Diaporama", result);
    }

    private async Task<ToolResult> ResizeAsync(string? folder, int width, int height, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Fail("Le paramètre 'folder' est requis.");

        var result = await _mediaService.ResizeImagesAsync(folder, width, height, ct);
        return FormatMediaResult("Redimensionnement", result);
    }

    private async Task<ToolResult> ThumbnailsAsync(string? videoPath, string? folder, int intervalSeconds, CancellationToken ct)
    {
        string targetFolder;
        if (!string.IsNullOrWhiteSpace(videoPath))
            targetFolder = Path.GetDirectoryName(videoPath) ?? "";
        else if (!string.IsNullOrWhiteSpace(folder))
            targetFolder = folder;
        else
            return Fail("Fournissez 'video_path' ou 'folder'.");

        var result = await _mediaService.CreateThumbnailsAsync(targetFolder, intervalSeconds, ct);
        return FormatMediaResult("Vignettes", result);
    }

    private async Task<ToolResult> WatermarkAsync(string? folder, string? watermarkPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Fail("Le paramètre 'folder' est requis.");
        if (string.IsNullOrWhiteSpace(watermarkPath))
            return Fail("Le paramètre 'watermark_path' est requis.");

        var result = await _mediaService.AddWatermarkAsync(folder, watermarkPath, ct);
        return FormatMediaResult("Filigrane", result);
    }

    private async Task<ToolResult> ExtractAudioAsync(string? videoPath, string? format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return Fail("Le paramètre 'video_path' est requis.");

        var result = await _mediaService.ExtractAudioAsync(videoPath, format ?? "mp3", ct);
        return FormatMediaResult("Extraction audio", result);
    }

    private async Task<ToolResult> GifAsync(string? videoPath, int startSeconds, int durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return Fail("Le paramètre 'video_path' est requis.");

        var result = await _mediaService.CreateGifAsync(videoPath, startSeconds, durationSeconds, ct);
        return FormatMediaResult("Création GIF", result);
    }

    private async Task<ToolResult> NormalizeAsync(string? folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Fail("Le paramètre 'folder' est requis.");

        var result = await _mediaService.NormalizeAudioAsync(folder, -16f, ct);
        return FormatMediaResult("Normalisation audio", result);
    }

    private async Task<ToolResult> SubtitlesAsync(string? videoPath, string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return Fail("Le paramètre 'video_path' est requis.");

        var result = await _mediaService.GenerateSubtitlesAsync(videoPath, language ?? "fr", ct);
        return FormatMediaResult("Sous-titres", result);
    }

    private async Task<ToolResult> ConvertAsync(string? image, string? format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(image))
            return Fail("Le paramètre 'image' est requis.");
        if (string.IsNullOrWhiteSpace(format))
            return Fail("Le paramètre 'source_format' (format cible, ex: png) est requis.");

        var targetFormat = format.TrimStart('.').ToLowerInvariant();
        var result = await _conversionService.ConvertImageAsync(image, targetFormat, ct);
        if (!result.Success)
            return Fail($"Échec de la conversion : {result.ErrorMessage}");

        return Ok($"Conversion terminée.\nFichier : {result.OutputPath}\nTaille : {FormatSize(result.OutputSizeBytes)}");
    }

    private ToolResult FormatMediaResult(string operation, MediaResult result)
    {
        if (!result.Success)
            return Fail($"Échec {operation.ToLowerInvariant()} : {result.ErrorMessage}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{operation} terminé.");
        sb.AppendLine($"Fichiers traités : {result.FilesProcessed}");
        if (!string.IsNullOrEmpty(result.OutputPath))
            sb.AppendLine($"Fichier : {result.OutputPath}");
        if (result.Errors.Count > 0)
        {
            sb.AppendLine($"Erreurs : {result.Errors.Count}");
            foreach (var error in result.Errors.Take(10))
                sb.AppendLine($"  - {error}");
        }
        return Ok(sb.ToString());
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
