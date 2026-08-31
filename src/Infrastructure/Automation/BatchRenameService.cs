using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Automation;

public interface IBatchRenameService
{
    Task<BatchRenameResult> RenameByPatternAsync(string folder, string pattern, string replacement, bool recursive = false, CancellationToken ct = default);
    Task<BatchRenameResult> RenameByDateAsync(string folder, string format = "yyyyMMdd_HHmmss", bool useCreationDate = false, CancellationToken ct = default);
    Task<BatchRenameResult> RenameSequentialAsync(string folder, string prefix, int startNumber = 1, int digits = 3, CancellationToken ct = default);
    Task<BatchRenameResult> RenameByRegexAsync(string folder, string regexPattern, string replacement, CancellationToken ct = default);
    List<RenamePreview> PreviewRename(string folder, string pattern, string replacement);
}

public sealed class BatchRenameService : IBatchRenameService
{
    private readonly ILogger<BatchRenameService> _logger;

    public BatchRenameService(ILogger<BatchRenameService> logger)
    {
        _logger = logger;
    }

    public async Task<BatchRenameResult> RenameByPatternAsync(string folder, string pattern, string replacement, bool recursive = false, CancellationToken ct = default)
    {
        var result = new BatchRenameResult { Folder = folder, Pattern = pattern, Replacement = replacement };

        if (!Directory.Exists(folder))
        {
            result.ErrorMessage = $"Folder not found: {folder}";
            return result;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(folder, "*", searchOption);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var fileName = Path.GetFileName(file);
                var newFileName = fileName.Replace(pattern, replacement, StringComparison.OrdinalIgnoreCase);

                if (newFileName != fileName)
                {
                    var newPath = Path.Combine(Path.GetDirectoryName(file)!, newFileName);
                    File.Move(file, newPath);
                    result.RenamedFiles++;
                    _logger.LogDebug("[BatchRename] {Old} → {New}", fileName, newFileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BatchRename] Failed to rename: {File}", file);
                result.Errors.Add(Path.GetFileName(file));
            }
        }

        result.Success = true;
        _logger.LogInformation("[BatchRename] Renamed {Count} files", result.RenamedFiles);
        return result;
    }

    public async Task<BatchRenameResult> RenameByDateAsync(string folder, string format = "yyyyMMdd_HHmmss", bool useCreationDate = false, CancellationToken ct = default)
    {
        var result = new BatchRenameResult { Folder = folder, Pattern = "date", Replacement = format };

        if (!Directory.Exists(folder))
        {
            result.ErrorMessage = $"Folder not found: {folder}";
            return result;
        }

        var files = Directory.GetFiles(folder);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var info = new FileInfo(file);
                var date = useCreationDate ? info.CreationTime : info.LastWriteTime;
                var dateStr = date.ToString(format);
                var ext = info.Extension;
                var newFileName = $"{dateStr}{ext}";
                var newPath = Path.Combine(folder, newFileName);

                // Handle duplicates
                var counter = 1;
                while (File.Exists(newPath))
                {
                    newFileName = $"{dateStr}_{counter}{ext}";
                    newPath = Path.Combine(folder, newFileName);
                    counter++;
                }

                File.Move(file, newPath);
                result.RenamedFiles++;
                _logger.LogDebug("[BatchRename] {Old} → {New}", info.Name, newFileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BatchRename] Failed to rename: {File}", file);
                result.Errors.Add(Path.GetFileName(file));
            }
        }

        result.Success = true;
        _logger.LogInformation("[BatchRename] Renamed {Count} files by date", result.RenamedFiles);
        return result;
    }

    public async Task<BatchRenameResult> RenameSequentialAsync(string folder, string prefix, int startNumber = 1, int digits = 3, CancellationToken ct = default)
    {
        var result = new BatchRenameResult { Folder = folder, Pattern = "sequential", Replacement = prefix };

        if (!Directory.Exists(folder))
        {
            result.ErrorMessage = $"Folder not found: {folder}";
            return result;
        }

        var files = Directory.GetFiles(folder).OrderBy(f => f).ToList();
        var number = startNumber;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var info = new FileInfo(file);
                var numStr = number.ToString().PadLeft(digits, '0');
                var newFileName = $"{prefix}{numStr}{info.Extension}";
                var newPath = Path.Combine(folder, newFileName);

                File.Move(file, newPath);
                result.RenamedFiles++;
                _logger.LogDebug("[BatchRename] {Old} → {New}", info.Name, newFileName);
                number++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BatchRename] Failed to rename: {File}", file);
                result.Errors.Add(Path.GetFileName(file));
            }
        }

        result.Success = true;
        _logger.LogInformation("[BatchRename] Renamed {Count} files sequentially", result.RenamedFiles);
        return result;
    }

    public async Task<BatchRenameResult> RenameByRegexAsync(string folder, string regexPattern, string replacement, CancellationToken ct = default)
    {
        var result = new BatchRenameResult { Folder = folder, Pattern = regexPattern, Replacement = replacement };

        if (!Directory.Exists(folder))
        {
            result.ErrorMessage = $"Folder not found: {folder}";
            return result;
        }

        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
        var files = Directory.GetFiles(folder);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var fileName = Path.GetFileName(file);
                if (regex.IsMatch(fileName))
                {
                    var newFileName = regex.Replace(fileName, replacement);
                    var newPath = Path.Combine(folder, newFileName);

                    File.Move(file, newPath);
                    result.RenamedFiles++;
                    _logger.LogDebug("[BatchRename] {Old} → {New}", fileName, newFileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BatchRename] Failed to rename: {File}", file);
                result.Errors.Add(Path.GetFileName(file));
            }
        }

        result.Success = true;
        _logger.LogInformation("[BatchRename] Renamed {Count} files by regex", result.RenamedFiles);
        return result;
    }

    public List<RenamePreview> PreviewRename(string folder, string pattern, string replacement)
    {
        var previews = new List<RenamePreview>();

        if (!Directory.Exists(folder))
            return previews;

        var files = Directory.GetFiles(folder);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var newFileName = fileName.Replace(pattern, replacement, StringComparison.OrdinalIgnoreCase);

            previews.Add(new RenamePreview
            {
                OriginalName = fileName,
                NewName = newFileName,
                WillChange = fileName != newFileName
            });
        }

        return previews;
    }
}

public sealed class BatchRenameResult
{
    public bool Success { get; set; }
    public string Folder { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Replacement { get; set; } = "";
    public int RenamedFiles { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public sealed class RenamePreview
{
    public string OriginalName { get; set; } = "";
    public string NewName { get; set; } = "";
    public bool WillChange { get; set; }
}
