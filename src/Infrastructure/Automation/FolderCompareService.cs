using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IFolderCompareService
{
    Task<CompareResult> CompareFoldersAsync(string path1, string path2, CancellationToken ct = default);
}

public sealed class FolderCompareService : IFolderCompareService
{
    private readonly ILogger<FolderCompareService> _logger;

    public FolderCompareService(ILogger<FolderCompareService> logger)
    {
        _logger = logger;
    }

    public async Task<CompareResult> CompareFoldersAsync(string path1, string path2, CancellationToken ct = default)
    {
        var result = new CompareResult { Path1 = path1, Path2 = path2 };

        if (!Directory.Exists(path1) || !Directory.Exists(path2))
        {
            result.ErrorMessage = "One or both folders not found";
            return result;
        }

        var files1 = Directory.GetFiles(path1, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(path1, f), f => new FileInfo(f));
        var files2 = Directory.GetFiles(path2, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(path2, f), f => new FileInfo(f));

        // Files only in path1
        result.FilesOnlyInPath1 = files1.Keys.Except(files2.Keys).ToList();

        // Files only in path2
        result.FilesOnlyInPath2 = files2.Keys.Except(files1.Keys).ToList();

        // Modified files
        foreach (var common in files1.Keys.Intersect(files2.Keys))
        {
            if (ct.IsCancellationRequested) break;

            var info1 = files1[common];
            var info2 = files2[common];

            if (info1.LastWriteTime != info2.LastWriteTime || info1.Length != info2.Length)
            {
                result.ModifiedFiles.Add(new ModifiedFile
                {
                    RelativePath = common,
                    Path1LastModified = info1.LastWriteTime,
                    Path2LastModified = info2.LastWriteTime,
                    Path1Size = info1.Length,
                    Path2Size = info2.Length
                });
            }
        }

        result.TotalFilesPath1 = files1.Count;
        result.TotalFilesPath2 = files2.Count;
        result.Success = true;

        _logger.LogInformation("[Compare] {P1}: {C1} files, {P2}: {C2} files, {Mod} modified",
            path1, files1.Count, path2, files2.Count, result.ModifiedFiles.Count);

        return result;
    }
}

public sealed class CompareResult
{
    public bool Success { get; set; }
    public string Path1 { get; set; } = "";
    public string Path2 { get; set; } = "";
    public int TotalFilesPath1 { get; set; }
    public int TotalFilesPath2 { get; set; }
    public List<string> FilesOnlyInPath1 { get; set; } = new();
    public List<string> FilesOnlyInPath2 { get; set; } = new();
    public List<ModifiedFile> ModifiedFiles { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public sealed class ModifiedFile
{
    public string RelativePath { get; set; } = "";
    public DateTime Path1LastModified { get; set; }
    public DateTime Path2LastModified { get; set; }
    public long Path1Size { get; set; }
    public long Path2Size { get; set; }
}
