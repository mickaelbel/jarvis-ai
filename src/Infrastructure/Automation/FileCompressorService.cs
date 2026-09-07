using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IFileCompressorService
{
    Task<CompressionReport> AnalyzeAsync(string path, CancellationToken ct = default);
    Task<List<CompressionCandidate>> GetOptimizationSuggestionsAsync(string path, int limit = 10, CancellationToken ct = default);
    Task<DataCompressionResult> CompressAsync(string sourcePath, string? outputPath = null, string format = "zip", bool recursive = true, CancellationToken ct = default);
}

public sealed class FileCompressorService : IFileCompressorService
{
    private readonly ILogger<FileCompressorService> _logger;

    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".mp4", ".mp3", ".jpg", ".jpeg", ".png", ".heic", ".avif", ".gif"
    };

    private static readonly HashSet<string> TextCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".json", ".xml", ".cs", ".ts", ".sql", ".bak"
    };

    private const long ThresholdBytes = 50L * 1024 * 1024;

    public FileCompressorService(ILogger<FileCompressorService> logger)
    {
        _logger = logger;
    }

    public async Task<CompressionReport> AnalyzeAsync(string path, CancellationToken ct = default)
    {
        var report = new CompressionReport { Path = path };
        var candidates = new List<CompressionCandidate>();

        await Task.Run(() =>
        {
            var searchOption = File.Exists(path) ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            var files = File.Exists(path)
                ? new[] { path }
                : Directory.GetFiles(path, "*.*", searchOption);

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var info = new FileInfo(file);
                    report.TotalSizeBytes += info.Length;

                    if (info.Length >= ThresholdBytes)
                    {
                        var ext = info.Extension;
                        string suggestion;
                        if (AlreadyCompressed.Contains(ext))
                            suggestion = "aucune";
                        else if (TextCodeExtensions.Contains(ext))
                            suggestion = "7z";
                        else
                            suggestion = "zip";

                        candidates.Add(new CompressionCandidate
                        {
                            FilePath = file,
                            Extension = ext,
                            SizeBytes = info.Length,
                            Suggestion = suggestion
                        });
                    }

                    report.FilesScanned++;
                }
                catch { }
            }
        }, ct);

        candidates = candidates.OrderByDescending(c => c.SizeBytes).Take(50).ToList();
        report.Candidates = candidates;
        report.PotentialSavingsBytes = candidates.Where(c => c.Suggestion != "aucune").Sum(c => c.SizeBytes / 4);
        return report;
    }

    public async Task<List<CompressionCandidate>> GetOptimizationSuggestionsAsync(string path, int limit = 10, CancellationToken ct = default)
    {
        var report = await AnalyzeAsync(path, ct);
        return report.Candidates.Where(c => c.Suggestion != "aucune").Take(limit).ToList();
    }

    public async Task<DataCompressionResult> CompressAsync(string sourcePath, string? outputPath = null, string format = "zip", bool recursive = true, CancellationToken ct = default)
    {
        var result = new DataCompressionResult
        {
            Format = format,
            InputBytes = GetInputSize(sourcePath, recursive)
        };

        try
        {
            if (format.Equals("zip", StringComparison.OrdinalIgnoreCase))
                await CompressZipAsync(sourcePath, outputPath, recursive, ct);
            else if (format.Equals("7z", StringComparison.OrdinalIgnoreCase))
                await Compress7zAsync(sourcePath, outputPath, ct);
            else if (format.Equals("rar", StringComparison.OrdinalIgnoreCase))
                await CompressRarAsync(sourcePath, outputPath, ct);
            else
            {
                result.ErrorMessage = $"Format non supporté : {format}";
                return result;
            }

            if (File.Exists(result.OutputPath ?? outputPath))
            {
                result.OutputPath = outputPath;
                result.OutputBytes = new FileInfo(result.OutputPath!).Length;
                result.Success = true;
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[FileCompressor] Erreur de compression");
        }

        return result;
    }

    private async Task CompressZipAsync(string sourcePath, string? outputPath, bool recursive, CancellationToken ct)
    {
        var extOut = Path.GetExtension(sourcePath);
        var outPath = outputPath ?? $"{sourcePath}.zip";

        if (File.Exists(sourcePath))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"jarvis_zip_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                File.Copy(sourcePath, Path.Combine(tempDir, Path.GetFileName(sourcePath)), true);
                ZipFile.CreateFromDirectory(tempDir, outPath, CompressionLevel.Optimal, false);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
        else if (Directory.Exists(sourcePath))
        {
            ZipFile.CreateFromDirectory(sourcePath, outPath, CompressionLevel.Optimal, true);
        }
        else
        {
            throw new FileNotFoundException($"Source introuvable : {sourcePath}");
        }

        await Task.CompletedTask;
    }

    private async Task Compress7zAsync(string sourcePath, string? outputPath, CancellationToken ct)
    {
        var sevenZipPath = FindTool("7z.exe", new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        });

        if (sevenZipPath is null)
            throw new InvalidOperationException("7-Zip introuvable. Installez 7-Zip ou ajoutez-le au PATH.");

        var outPath = outputPath ?? $"{sourcePath}.7z";
        await RunProcessAsync(sevenZipPath, $"a -mx=9 \"{outPath}\" \"{sourcePath}\"", ct);
    }

    private async Task CompressRarAsync(string sourcePath, string? outputPath, CancellationToken ct)
    {
        var rarPath = FindTool("rar.exe", new[]
        {
            @"C:\Program Files\WinRAR\rar.exe",
            @"C:\Program Files (x86)\WinRAR\rar.exe"
        });

        if (rarPath is null)
            throw new InvalidOperationException("WinRAR introuvable. Installez WinRAR ou ajoutez rar.exe au PATH.");

        var outPath = outputPath ?? $"{sourcePath}.rar";
        await RunProcessAsync(rarPath, $"a -m5 -ep1 \"{outPath}\" \"{sourcePath}\"", ct);
    }

    private async Task RunProcessAsync(string executable, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Erreur {Path.GetFileNameWithoutExtension(executable)} (code {process.ExitCode}) : {error}");
        }
    }

    private static long GetInputSize(string path, bool recursive)
    {
        if (File.Exists(path))
            return new FileInfo(path).Length;

        if (Directory.Exists(path))
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.GetFiles(path, "*", option)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });
        }

        return 0;
    }

    private static string? FindTool(string exe, string[] knownPaths)
    {
        foreach (var p in knownPaths)
        {
            if (File.Exists(p)) return p;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

public sealed class CompressionReport
{
    public string Path { get; set; } = "";
    public int FilesScanned { get; set; }
    public long TotalSizeBytes { get; set; }
    public List<CompressionCandidate> Candidates { get; set; } = new();
    public long PotentialSavingsBytes { get; set; }
}

public sealed class CompressionCandidate
{
    public string FilePath { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Suggestion { get; set; } = "";
}

public sealed class DataCompressionResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }
    public string Format { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public long SavingsBytes => InputBytes - OutputBytes;
}
