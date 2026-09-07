using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IFileConversionService
{
    Task<ConversionResult> ConvertImageAsync(string inputPath, string outputFormat, CancellationToken ct = default);
    Task<ConversionResult> ConvertBatchAsync(string folder, string inputPattern, string outputFormat, CancellationToken ct = default);
    Task<ConversionResult> ResizeImageAsync(string inputPath, int width, int height, CancellationToken ct = default);
    Task<List<string>> GetSupportedImageFormats();
    Task<List<string>> GetSupportedAudioFormats();
    Task<ConversionResult> ExtractAudioAsync(string videoPath, string outputFormat = "mp3", CancellationToken ct = default);
}

public sealed class FileConversionService : IFileConversionService
{
    private readonly ILogger<FileConversionService> _logger;

    private static readonly HashSet<string> ImageFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp", ".heic", ".ico", ".tiff", ".avif"
    };

    private static readonly HashSet<string> AudioFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a"
    };

    private static readonly HashSet<string> VideoFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm"
    };

    public FileConversionService(ILogger<FileConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<ConversionResult> ConvertImageAsync(string inputPath, string outputFormat, CancellationToken ct = default)
    {
        var result = new ConversionResult { InputPath = inputPath };

        if (!File.Exists(inputPath))
        {
            result.ErrorMessage = $"File not found: {inputPath}";
            return result;
        }

        var ext = Path.GetExtension(inputPath);
        if (!ImageFormats.Contains(ext))
        {
            result.ErrorMessage = $"Not an image file: {ext}";
            return result;
        }

        var outputPath = Path.ChangeExtension(inputPath, outputFormat);
        result.OutputPath = outputPath;

        try
        {
            var inputExt = Path.GetExtension(inputPath);
            var outputNorm = outputFormat.TrimStart('.').ToLowerInvariant();
            var ffmpegFormats = new[] { ".heic", ".webp", ".avif", "heic", "webp", "avif" };

            if (ffmpegFormats.Contains(inputExt, StringComparer.OrdinalIgnoreCase) ||
                ffmpegFormats.Contains(outputNorm, StringComparer.OrdinalIgnoreCase))
            {
                await ConvertWithFfmpegAsync(inputPath, outputPath, ct);
                result.Success = File.Exists(outputPath);
                if (!result.Success)
                    result.ErrorMessage = "La conversion ffmpeg n'a pas produit de fichier.";
                else
                    result.OutputSizeBytes = new FileInfo(outputPath).Length;
                _logger.LogInformation("[Conversion] {Input} → {Output}", inputPath, outputPath);
                return result;
            }

            // Use System.Drawing for conversion
            using var image = System.Drawing.Image.FromFile(inputPath);
            var format = GetImageFormat(outputFormat);
            image.Save(outputPath, format);

            result.Success = true;
            result.OutputSizeBytes = new FileInfo(outputPath).Length;
            _logger.LogInformation("[Conversion] {Input} → {Output}", inputPath, outputPath);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Conversion] Failed: {Input}", inputPath);
        }

        return result;
    }

    private async Task ConvertWithFfmpegAsync(string input, string output, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        if (string.IsNullOrEmpty(ffmpeg))
            throw new Exception("ffmpeg n'est pas disponible pour cette conversion.");

        var isAvif = Path.GetExtension(output).Equals(".avif", StringComparison.OrdinalIgnoreCase);
        var extraArgs = isAvif ? " -c:v libaom-av1 -still-picture 1" : "";
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -i \"{input}\"{extraArgs} \"{output}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process is null) throw new Exception("Impossible de démarrer ffmpeg.");

        await process.WaitForExitAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception(isAvif
                ? $"ffmpeg sans support AVIF : {stderr}"
                : $"ffmpeg a échoué : {stderr}");
    }

    public async Task<ConversionResult> ConvertBatchAsync(string folder, string inputPattern, string outputFormat, CancellationToken ct = default)
    {
        var result = new ConversionResult { InputPath = folder };

        if (!Directory.Exists(folder))
        {
            result.ErrorMessage = $"Folder not found: {folder}";
            return result;
        }

        var files = Directory.GetFiles(folder, inputPattern);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            var fileResult = await ConvertImageAsync(file, outputFormat, ct);
            if (fileResult.Success)
                result.FilesConverted++;
            else
                result.Errors.Add(Path.GetFileName(file));
        }

        result.Success = true;
        _logger.LogInformation("[Conversion] Batch converted {Count} files", result.FilesConverted);
        return result;
    }

    public async Task<ConversionResult> ResizeImageAsync(string inputPath, int width, int height, CancellationToken ct = default)
    {
        var result = new ConversionResult { InputPath = inputPath };

        if (!File.Exists(inputPath))
        {
            result.ErrorMessage = $"File not found: {inputPath}";
            return result;
        }

        var outputPath = Path.Combine(
            Path.GetDirectoryName(inputPath)!,
            $"{Path.GetFileNameWithoutExtension(inputPath)}_{width}x{height}{Path.GetExtension(inputPath)}");
        result.OutputPath = outputPath;

        try
        {
            using var image = System.Drawing.Image.FromFile(inputPath);
            using var resized = new System.Drawing.Bitmap(width, height);

            using (var graphics = System.Drawing.Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, 0, 0, width, height);
            }

            resized.Save(outputPath, image.RawFormat);

            result.Success = true;
            result.OutputSizeBytes = new FileInfo(outputPath).Length;
            _logger.LogInformation("[Conversion] Resized: {Input} → {Output}", inputPath, outputPath);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Conversion] Resize failed: {Input}", inputPath);
        }

        return result;
    }

    public Task<List<string>> GetSupportedImageFormats()
    {
        return Task.FromResult(ImageFormats.ToList());
    }

    public Task<List<string>> GetSupportedAudioFormats()
    {
        return Task.FromResult(AudioFormats.ToList());
    }

    public async Task<ConversionResult> ExtractAudioAsync(string videoPath, string outputFormat = "mp3", CancellationToken ct = default)
    {
        var result = new ConversionResult { InputPath = videoPath };

        if (!File.Exists(videoPath))
        {
            result.ErrorMessage = $"File not found: {videoPath}";
            return result;
        }

        var ext = Path.GetExtension(videoPath);
        if (!VideoFormats.Contains(ext))
        {
            result.ErrorMessage = $"Not a video file: {ext}";
            return result;
        }

        var outputPath = Path.ChangeExtension(videoPath, outputFormat);
        result.OutputPath = outputPath;

        try
        {
            // Try ffmpeg
            var ffmpeg = FindFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                result.ErrorMessage = "ffmpeg not found. Install ffmpeg to extract audio.";
                return result;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{videoPath}\" -vn -acodec libmp3lame -q:a 2 \"{outputPath}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync(ct);

                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    result.Success = true;
                    result.OutputSizeBytes = new FileInfo(outputPath).Length;
                    _logger.LogInformation("[Conversion] Extracted audio: {Input} → {Output}", videoPath, outputPath);
                }
                else
                {
                    var error = await process.StandardError.ReadToEndAsync(ct);
                    result.ErrorMessage = $"ffmpeg failed: {error}";
                }
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Conversion] Audio extraction failed: {Input}", videoPath);
        }

        return result;
    }

    private static System.Drawing.Imaging.ImageFormat GetImageFormat(string format)
    {
        return format.ToLowerInvariant() switch
        {
            ".jpg" or "jpg" or "jpeg" => System.Drawing.Imaging.ImageFormat.Jpeg,
            ".png" or "png" => System.Drawing.Imaging.ImageFormat.Png,
            ".gif" or "gif" => System.Drawing.Imaging.ImageFormat.Gif,
            ".bmp" or "bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
            ".tiff" or "tiff" or "tif" => System.Drawing.Imaging.ImageFormat.Tiff,
            _ => System.Drawing.Imaging.ImageFormat.Png
        };
    }

    private static string? FindFfmpeg()
    {
        // Check common locations
        var paths = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ffmpeg", "bin", "ffmpeg.exe"),
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
                return path;
        }

        // Try PATH
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                process.WaitForExit(2000);
                if (process.ExitCode == 0)
                    return "ffmpeg";
            }
        }
        catch { }

        return null;
    }
}

public sealed class ConversionResult
{
    public bool Success { get; set; }
    public string InputPath { get; set; } = "";
    public string? OutputPath { get; set; }
    public long OutputSizeBytes { get; set; }
    public int FilesConverted { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
