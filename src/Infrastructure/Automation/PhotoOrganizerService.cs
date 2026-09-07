using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IPhotoOrganizerService
{
    Task<PhotoOrganizationResult> OrganizeByDateAsync(string sourceFolder, string destinationRoot, CancellationToken ct = default);
    Task<PhotoOrganizationResult> StripMetadataAsync(string folder, CancellationToken ct = default);
}

public sealed class PhotoOrganizerService : IPhotoOrganizerService
{
    private readonly ILogger<PhotoOrganizerService> _logger;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png"
    };

    public PhotoOrganizerService(ILogger<PhotoOrganizerService> logger)
    {
        _logger = logger;
    }

    public async Task<PhotoOrganizationResult> OrganizeByDateAsync(string sourceFolder, string destinationRoot, CancellationToken ct = default)
    {
        var result = new PhotoOrganizationResult { DestinationRoot = destinationRoot };

        if (!Directory.Exists(sourceFolder))
        {
            result.ErrorMessage = $"Dossier source introuvable : {sourceFolder}";
            return result;
        }

        var files = Directory.GetFiles(sourceFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var date = GetPhotoDate(file);
                    var isExif = HasExifDate(file);
                    var targetDir = Path.Combine(destinationRoot, date.Year.ToString("D4"), date.Month.ToString("D2"), date.Day.ToString("D2"));
                    Directory.CreateDirectory(targetDir);

                    var destFile = Path.Combine(targetDir, Path.GetFileName(file));

                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                    {
                        var tempFile = Path.Combine(targetDir, $"_tmp_{Guid.NewGuid():N}{Path.GetExtension(file)}");
                        File.Copy(file, tempFile, false);
                        File.Move(tempFile, destFile, false);
                    }
                    else
                    {
                        destFile = GetUniquePath(destFile);
                        File.Move(file, destFile, false);
                    }

                    result.FilesProcessed++;
                    if (isExif) result.OrganizedByExif++;
                    else result.OrganizedByFileDate++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{file} : {ex.Message}");
                    _logger.LogWarning(ex, "[PhotoOrganizer] Erreur sur {File}", file);
                }
            }
        }, ct);

        result.Success = true;
        _logger.LogInformation("[PhotoOrganizer] {Processed} fichiers traités depuis {Src}", result.FilesProcessed, sourceFolder);
        return result;
    }

    public async Task<PhotoOrganizationResult> StripMetadataAsync(string folder, CancellationToken ct = default)
    {
        var result = new PhotoOrganizationResult();

        if (!Directory.Exists(folder))
        {
            result.ErrorMessage = $"Dossier introuvable : {folder}";
            return result;
        }

        var files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    using var img = Image.FromFile(file);

                    if (img.PropertyIdList.Length == 0)
                    {
                        result.FilesProcessed++;
                        continue;
                    }

                    foreach (var propId in img.PropertyIdList)
                    {
                        try { img.RemovePropertyItem(propId); } catch { }
                    }

                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".jpg" or ".jpeg")
                        SaveJpeg(img, file);
                    else
                        img.Save(file, ImageFormat.Png);

                    result.FilesProcessed++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{file} : {ex.Message}");
                    _logger.LogWarning(ex, "[PhotoOrganizer] Erreur strip metadata {File}", file);
                }
            }
        }, ct);

        result.Success = true;
        _logger.LogInformation("[PhotoOrganizer] Métadonnées stripées sur {Count} fichiers dans {Folder}", result.FilesProcessed, folder);
        return result;
    }

    private static DateTime GetPhotoDate(string filePath)
    {
        try
        {
            using var img = Image.FromFile(filePath);
            var prop = img.GetPropertyItem(0x9003);
            var dateStr = System.Text.Encoding.ASCII.GetString(prop.Value).TrimEnd('\0');
            if (DateTime.TryParseExact(dateStr, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
        }
        catch { }

        return File.GetLastWriteTime(filePath);
    }

    private static bool HasExifDate(string filePath)
    {
        try
        {
            using var img = Image.FromFile(filePath);
            _ = img.GetPropertyItem(0x9003);
            return true;
        }
        catch { return false; }
    }

    private static void SaveJpeg(Image img, string path)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var eps = new EncoderParameters(1);
        eps.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
        img.Save(path, codec, eps);
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var counter = 1;

        while (File.Exists(Path.Combine(dir, $"{name}_{counter}{ext}")))
            counter++;

        return Path.Combine(dir, $"{name}_{counter}{ext}");
    }
}

public sealed class PhotoOrganizationResult
{
    public bool Success { get; set; }
    public int FilesProcessed { get; set; }
    public int OrganizedByExif { get; set; }
    public int OrganizedByFileDate { get; set; }
    public List<string> Errors { get; set; } = new();
    public string DestinationRoot { get; set; } = "";
    public string? ErrorMessage { get; set; }
}
