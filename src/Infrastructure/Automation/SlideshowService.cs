using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;

namespace JarvisAI.Infrastructure.Automation;

public interface ISlideshowService
{
    Task<MediaResult> CreateSlideshowAsync(IReadOnlyList<string> imagePaths, string outputPath, string? musicPath = null, double transitionSeconds = 1, double slideDurationSeconds = 4, CancellationToken ct = default);
}

public sealed class SlideshowService : ISlideshowService
{
    private readonly ILogger<SlideshowService> _logger;
    private readonly string _ffmpegPath;

    public SlideshowService(ILogger<SlideshowService> logger)
    {
        _logger = logger;
        _ffmpegPath = FindFfmpeg() ?? "ffmpeg";
    }

    public async Task<MediaResult> CreateSlideshowAsync(IReadOnlyList<string> imagePaths, string outputPath, string? musicPath = null, double transitionSeconds = 1, double slideDurationSeconds = 4, CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Create Slideshow" };

        var images = imagePaths?.Take(100).ToList() ?? new List<string>();
        if (images.Count == 0)
        {
            result.ErrorMessage = "Aucune image à traiter.";
            return result;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"jarvis_slideshow_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var n = images.Count;
            var parts = new List<string>(n);

            for (var i = 0; i < n; i++)
            {
                if (ct.IsCancellationRequested) break;
                var img = images[i];
                if (!File.Exists(img)) throw new Exception($"Image introuvable : {img}");

                var partPath = Path.Combine(tempDir, $"part_{i:D2}.mp4");
                var args = $"-y -loop 1 -t {slideDurationSeconds.ToString("0.####", CultureInfo.InvariantCulture)} -i \"{img}\" -vf \"scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,format=yuv420p\" -an -c:v libx264 -crf 20 -preset fast \"{partPath}\"";
                await RunProcessAsync(args, ct);
                parts.Add(partPath);
                result.FilesProcessed++;
            }

            if (ct.IsCancellationRequested)
            {
                result.ErrorMessage = "Opération annulée.";
                return result;
            }

            var d = slideDurationSeconds;
            var f = transitionSeconds;
            var totalDuration = n * d - (n - 1) * f;
            var constI = CultureInfo.InvariantCulture;
            var doubleF = f.ToString("0.####", constI);

            var chain = "";
            for (var k = 1; k < n; k++)
            {
                var offset = (k * (d - f)).ToString("0.####", constI);
                chain = k == 1
                    ? $"[0:v][1:v]xfade=transition=fade:duration={doubleF}:offset={offset}[v1]"
                    : $"[v{k - 1}][{k}:v]xfade=transition=fade:duration={doubleF}:offset={offset}[v{k}]";
            }

            var videoLabel = $"v{n - 1}";
            var totalStr = totalDuration.ToString("0.####", constI);
            var maps = $"-map \"[{videoLabel}]\"";
            if (!string.IsNullOrEmpty(musicPath))
            {
                chain += $";[{n}:a]atrim=0:{totalStr},asetpts=PTS-STARTPTS,volume=0.9[aout]";
                maps = $"-map \"[{videoLabel}]\" -map \"[aout]\"";
            }

            var outputArgs = $"-filter_complex \"{chain}\" {maps} -c:v libx264 -crf 20 -preset fast";
            if (!string.IsNullOrEmpty(musicPath))
                outputArgs += " -c:a aac -b:a 192k";
            outputArgs += " -movflags +faststart";

            var finalArgs = $" -y -i \"{parts[0]}\"";
            for (var i = 1; i < n; i++)
                finalArgs += $" -i \"{parts[i]}\"";
            if (!string.IsNullOrEmpty(musicPath))
                finalArgs += $" -i \"{musicPath}\"";

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await RunProcessAsync($"{finalArgs} {outputArgs} \"{outputPath}\"", ct);

            result.Success = File.Exists(outputPath);
            result.OutputPath = outputPath;
            if (!result.Success)
                result.ErrorMessage = "La sortie du diaporama n'a pas été créée.";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch { }
        }

        return result;
    }

    private async Task RunProcessAsync(string arguments, CancellationToken ct)
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
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception($"ffmpeg a échoué : {stderr}");
    }

    private static string? FindFfmpeg()
    {
        var paths = new[] { @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe" };
        foreach (var path in paths)
            if (File.Exists(path)) return path;
        return null;
    }
}
