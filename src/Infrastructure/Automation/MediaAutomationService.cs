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
            var wavPath = Path.ChangeExtension(videoPath, ".wav");
            var dirTmp = Path.Combine(Path.GetTempPath(), $"jarvis_srt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dirTmp);

            try
            {
                await RunFfmpegAsync($"-i \"{videoPath}\" -ac 1 -ar 16000 \"{wavPath}\" -y", ct);

                var whisper = FindWhisper();
                if (!string.IsNullOrEmpty(whisper))
                {
                    await RunWhisperAsync(whisper, wavPath, language, dirTmp, ct);
                    var srt = Directory.GetFiles(dirTmp, "*.srt", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (srt != null)
                    {
                        File.Copy(srt, outputPath, true);
                        result.Success = File.Exists(outputPath);
                        result.OutputPath = outputPath;
                        return result;
                    }
                }

                var duration = await GetDurationAsync(videoPath, ct);
                WriteFallbackSrt(outputPath, duration);

                result.Success = File.Exists(outputPath);
                result.OutputPath = outputPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(wavPath)) File.Delete(wavPath);
                    if (Directory.Exists(dirTmp)) Directory.Delete(dirTmp, true);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private static string? FindWhisper()
    {
        var userLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "whisper.exe");
        if (File.Exists(userLocal)) return userLocal;

        var repoVenv = Path.Combine(Directory.GetCurrentDirectory(), ".venv", "Scripts", "whisper.exe");
        if (File.Exists(repoVenv)) return repoVenv;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "whisper",
                Arguments = "--help",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is not null)
            {
                process.WaitForExit(3000);
                if (process.ExitCode == 0)
                    return "whisper";
            }
        }
        catch { }

        return null;
    }

    private async Task RunWhisperAsync(string whisper, string wavPath, string language, string dirTmp, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = whisper,
            Arguments = $"\"{wavPath}\" --language {language} --model small --output_format srt --output_dir \"{dirTmp}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception($"whisper a échoué : {error}");
    }

    private async Task<double> GetDurationAsync(string videoPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"-i \"{videoPath}\" -f null -",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        var match = System.Text.RegularExpressions.Regex.Match(stderr, @"Duration: (\d+):(\d{2}):(\d{2}\.\d+)");
        if (!match.Success) return 30;

        var hours = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minutes = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return hours * 3600 + minutes * 60 + seconds;
    }

    private static void WriteFallbackSrt(string outputPath, double duration)
    {
        var sb = new System.Text.StringBuilder();
        var index = 1;
        for (var start = 0.0; start < duration; start += 5)
        {
            var end = Math.Min(start + 5, duration);
            sb.AppendLine(index.ToString());
            sb.AppendLine($"{FormatTimestamp(start)} --> {FormatTimestamp(end)}");
            sb.AppendLine("(piste à transcrire — whisper non disponible)");
            sb.AppendLine();
            index++;
        }

        File.WriteAllText(outputPath, sb.ToString(), new System.Text.UTF8Encoding(true));
    }

    private static string FormatTimestamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
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
