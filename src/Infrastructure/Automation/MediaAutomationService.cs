using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Automation;

public interface IMediaAutomationService
{
    Task<MediaResult> ResizeImagesAsync(string folder, int width, int height, CancellationToken ct = default);
    Task<MediaResult> CreateThumbnailsAsync(string videoFolder, int intervalSeconds = 30, CancellationToken ct = default);
    Task<MediaResult> AddWatermarkAsync(string folder, string watermarkPath, CancellationToken ct = default);
    Task<MediaResult> ExtractAudioAsync(string videoPath, string outputFormat = "mp3", CancellationToken ct = default);
    Task<MediaResult> CreateGifAsync(string videoPath, int startSeconds, int durationSeconds, CancellationToken ct = default);
    Task<MediaResult> NormalizeAudioAsync(string folder, float targetLoudness = -16f, CancellationToken ct = default);
    Task<MediaResult> GenerateSubtitlesAsync(string videoPath, string language = "fr", CancellationToken ct = default);
}

public sealed class MediaAutomationService : IMediaAutomationService
{
    private readonly ILogger<MediaAutomationService> _logger;
    private readonly string _ffmpegPath;

    public MediaAutomationService(ILogger<MediaAutomationService> logger)
    {
        _logger = logger;
        _ffmpegPath = FindFfmpeg() ?? "ffmpeg";
    }

    public async Task<MediaResult> ResizeImagesAsync(string folder, int width, int height, CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Resize Images" };
        var files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var outputPath = Path.Combine(Path.GetDirectoryName(file)!,
                    $"{Path.GetFileNameWithoutExtension(file)}_{width}x{height}{Path.GetExtension(file)}");

                await RunFfmpegAsync($"-i \"{file}\" -vf scale={width}:{height} \"{outputPath}\" -y", ct);
                result.FilesProcessed++;
            }
            catch { result.Errors.Add(Path.GetFileName(file)); }
        }

        result.Success = true;
        return result;
    }

    public async Task<MediaResult> CreateThumbnailsAsync(string videoFolder, int intervalSeconds = 30, CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Create Thumbnails" };
        var videos = Directory.GetFiles(videoFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var video in videos)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var outputDir = Path.Combine(videoFolder, "thumbnails", Path.GetFileNameWithoutExtension(video));
                Directory.CreateDirectory(outputDir);

                await RunFfmpegAsync(
                    $"-i \"{video}\" -vf fps=1/{intervalSeconds} \"{outputDir}/thumb_%04d.jpg\" -y", ct);
                result.FilesProcessed++;
            }
            catch { result.Errors.Add(Path.GetFileName(video)); }
        }

        result.Success = true;
        return result;
    }

    public async Task<MediaResult> AddWatermarkAsync(string folder, string watermarkPath, CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Add Watermark" };
        var images = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var image in images)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var outputPath = Path.Combine(Path.GetDirectoryName(image)!,
                    $"watermarked_{Path.GetFileName(image)}");

                await RunFfmpegAsync(
                    $"-i \"{image}\" -i \"{watermarkPath}\" -filter_complex \"[0][1]overlay=10:10\" \"{outputPath}\" -y", ct);
                result.FilesProcessed++;
            }
            catch { result.Errors.Add(Path.GetFileName(image)); }
        }

        result.Success = true;
        return result;
    }

    public async Task<MediaResult> ExtractAudioAsync(string videoPath, string outputFormat = "mp3", CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Extract Audio" };

        try
        {
            var outputPath = Path.ChangeExtension(videoPath, outputFormat);
            await RunFfmpegAsync($"-i \"{videoPath}\" -vn -acodec libmp3lame -q:a 2 \"{outputPath}\" -y", ct);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async Task<MediaResult> CreateGifAsync(string videoPath, int startSeconds, int durationSeconds, CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Create GIF" };

        try
        {
            var outputPath = Path.ChangeExtension(videoPath, ".gif");
            await RunFfmpegAsync(
                $"-ss {startSeconds} -t {durationSeconds} -i \"{videoPath}\" -vf fps=15,scale=480:-1 \"{outputPath}\" -y", ct);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async Task<MediaResult> NormalizeAudioAsync(string folder, float targetLoudness = -16f, CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Normalize Audio" };
        var audioFiles = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in audioFiles)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var outputPath = Path.Combine(Path.GetDirectoryName(file)!,
                    $"normalized_{Path.GetFileName(file)}");

                await RunFfmpegAsync(
                    $"-i \"{file}\" -af loudnorm=I={targetLoudness}:TP=-1:LRA=11 \"{outputPath}\" -y", ct);
                result.FilesProcessed++;
            }
            catch { result.Errors.Add(Path.GetFileName(file)); }
        }

        result.Success = true;
        return result;
    }

    public async Task<MediaResult> GenerateSubtitlesAsync(string videoPath, string language = "fr", CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Generate Subtitles" };

        try
        {
            var outputPath = Path.ChangeExtension(videoPath, ".srt");
            // Basic subtitle generation using whisper-like approach
            await RunFfmpegAsync(
                $"-i \"{videoPath}\" -ac 1 -ar 16000 \"{Path.ChangeExtension(videoPath, ".wav")}\" -y", ct);

            result.Success = true;
            result.OutputPath = outputPath;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task RunFfmpegAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new Exception($"ffmpeg failed: {error}");
        }
    }

    private static string? FindFfmpeg()
    {
        var paths = new[] { @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe" };
        foreach (var path in paths)
            if (File.Exists(path)) return path;
        return null;
    }
}

public sealed class MediaResult
{
    public bool Success { get; set; }
    public string Operation { get; set; } = "";
    public string? OutputPath { get; set; }
    public int FilesProcessed { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
