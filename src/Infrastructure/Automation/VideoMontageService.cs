using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;

namespace JarvisAI.Infrastructure.Automation;

public interface IVideoMontageService
{
    Task<MediaResult> CreateMontageAsync(IReadOnlyList<string> clipPaths, string outputPath, string? musicPath = null, double transitionSeconds = 1, double clipDurationSeconds = 8, CancellationToken ct = default);
}

public sealed class VideoMontageService : IVideoMontageService
{
    private readonly ILogger<VideoMontageService> _logger;
    private readonly string _ffmpegPath;

    public VideoMontageService(ILogger<VideoMontageService> logger)
    {
        _logger = logger;
        _ffmpegPath = FindFfmpeg() ?? "ffmpeg";
    }

    public async Task<MediaResult> CreateMontageAsync(IReadOnlyList<string> clipPaths, string outputPath, string? musicPath = null, double transitionSeconds = 1, double clipDurationSeconds = 8, CancellationToken ct = default)
    {
        var result = new MediaResult { Operation = "Create Montage" };

        if (clipPaths == null || clipPaths.Count < 2)
        {
            result.ErrorMessage = "Au moins deux clips sont nécessaires pour créer un montage.";
            return result;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"jarvis_montage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var n = clipPaths.Count;
            var parts = new List<string>(n);

            for (var i = 0; i < n; i++)
            {
                if (ct.IsCancellationRequested) break;
                var clip = clipPaths[i];
                if (!File.Exists(clip)) throw new Exception($"Clip introuvable : {clip}");

                var partPath = Path.Combine(tempDir, $"part_{i:D2}.mp4");
                var args = $"-y -i \"{clip}\" -t {clipDurationSeconds.ToString("0.####", CultureInfo.InvariantCulture)} -vf \"scale=1280:-2,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p\" -an -c:v libx264 -crf 20 -preset fast \"{partPath}\"";
                await RunProcessAsync(args, ct);
                parts.Add(partPath);
                result.FilesProcessed++;
            }

            if (ct.IsCancellationRequested)
            {
                result.ErrorMessage = "Opération annulée.";
                return result;
            }

            var d = clipDurationSeconds;
            var f = transitionSeconds;
            var totalDuration = n * d - (n - 1) * f;

            var chain = "[0:v][1:v]";
            var doubleF = f.ToString("0.####", CultureInfo.InvariantCulture);
            var constI = CultureInfo.InvariantCulture;
            for (var k = 1; k < n; k++)
            {
                var offset = (k * (d - f)).ToString("0.####", constI);
                chain = k == 1
                    ? $"[0:v][1:v]xfade=transition=fade:duration={doubleF}:offset={offset}[v1]"
                    : $"[v{k - 1}][{k}:v]xfade=transition=fade:duration={doubleF}:offset={offset}[v{k}]";
            }

            var videoLabel = $"v{n - 1}";

            var maps = $"-map \"[{videoLabel}]\"";
            var totalStr = totalDuration.ToString("0.####", constI);

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

            var full = $"{finalArgs} {outputArgs} \"{outputPath}\"";

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await RunProcessAsync(full, ct);

            result.Success = File.Exists(outputPath);
            result.OutputPath = outputPath;
            if (!result.Success)
                result.ErrorMessage = "La sortie du montage n'a pas été créée.";
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
