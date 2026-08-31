using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IFileOrganizationService
{
    Task<OrganizationResult> OrganizeDownloadsAsync(string? downloadsPath = null, CancellationToken ct = default);
    Task<OrganizationResult> OrganizeFolderAsync(string path, string pattern, CancellationToken ct = default);
    Task<List<FileCategory>> GetCategoriesAsync();
    string GetCategoryForFile(string filePath);
}

public sealed class FileOrganizationService : IFileOrganizationService
{
    private readonly ILogger<FileOrganizationService> _logger;

    private static readonly Dictionary<string, string> ExtensionCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        [".jpg"] = "Images",
        [".jpeg"] = "Images",
        [".png"] = "Images",
        [".gif"] = "Images",
        [".bmp"] = "Images",
        [".svg"] = "Images",
        [".webp"] = "Images",
        [".heic"] = "Images",
        [".ico"] = "Images",

        // Videos
        [".mp4"] = "Vidéos",
        [".avi"] = "Vidéos",
        [".mkv"] = "Vidéos",
        [".mov"] = "Vidéos",
        [".wmv"] = "Vidéos",
        [".flv"] = "Vidéos",
        [".webm"] = "Vidéos",

        // Audio
        [".mp3"] = "Audio",
        [".wav"] = "Audio",
        [".flac"] = "Audio",
        [".aac"] = "Audio",
        [".ogg"] = "Audio",
        [".wma"] = "Audio",

        // Documents
        [".pdf"] = "Documents",
        [".doc"] = "Documents",
        [".docx"] = "Documents",
        [".txt"] = "Documents",
        [".rtf"] = "Documents",
        [".odt"] = "Documents",

        // Spreadsheets
        [".xls"] = "Tableurs",
        [".xlsx"] = "Tableurs",
        [".csv"] = "Tableurs",
        [".ods"] = "Tableurs",

        // Presentations
        [".ppt"] = "Présentations",
        [".pptx"] = "Présentations",
        [".odp"] = "Présentations",

        // Archives
        [".zip"] = "Archives",
        [".rar"] = "Archives",
        [".7z"] = "Archives",
        [".tar"] = "Archives",
        [".gz"] = "Archives",

        // Executables
        [".exe"] = "Exécutables",
        [".msi"] = "Exécutables",
        [".bat"] = "Exécutables",
        [".cmd"] = "Exécutables",
        [".ps1"] = "Exécutables",

        // Code
        [".cs"] = "Code",
        [".js"] = "Code",
        [".ts"] = "Code",
        [".py"] = "Code",
        [".java"] = "Code",
        [".cpp"] = "Code",
        [".c"] = "Code",
        [".h"] = "Code",
        [".html"] = "Code",
        [".css"] = "Code",
        [".json"] = "Code",
        [".xml"] = "Code",
        [".yaml"] = "Code",
        [".yml"] = "Code",

        // 3D / Blender
        [".blend"] = "Blender",
        [".blend1"] = "Blender",
        [".obj"] = "3D",
        [".fbx"] = "3D",
        [".gltf"] = "3D",
        [".glb"] = "3D",
        [".stl"] = "3D",

        // Fonts
        [".ttf"] = "Polices",
        [".otf"] = "Polices",
        [".woff"] = "Polices",
        [".woff2"] = "Polices"
    };

    public FileOrganizationService(ILogger<FileOrganizationService> logger)
    {
        _logger = logger;
    }

    public async Task<OrganizationResult> OrganizeDownloadsAsync(string? downloadsPath = null, CancellationToken ct = default)
    {
        var path = downloadsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        return await OrganizeFolderAsync(path, null, ct);
    }

    public async Task<OrganizationResult> OrganizeFolderAsync(string path, string? pattern, CancellationToken ct = default)
    {
        var result = new OrganizationResult { SourcePath = path };

        if (!Directory.Exists(path))
        {
            result.ErrorMessage = $"Folder not found: {path}";
            return result;
        }

        var files = Directory.GetFiles(path);
        result.TotalFiles = files.Length;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var category = GetCategoryForFile(file);
                var fileName = Path.GetFileName(file);
                var destDir = Path.Combine(path, category);

                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                var destPath = Path.Combine(destDir, fileName);

                // Handle duplicates
                if (File.Exists(destPath))
                {
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                    var ext = Path.GetExtension(file);
                    var counter = 1;

                    do
                    {
                        destPath = Path.Combine(destDir, $"{nameWithoutExt}_{counter}{ext}");
                        counter++;
                    } while (File.Exists(destPath));
                }

                File.Move(file, destPath);
                result.MovedFiles++;
                result.FilesByCategory[category] = result.FilesByCategory.GetValueOrDefault(category) + 1;

                _logger.LogDebug("[FileOrg] Moved: {File} → {Category}", fileName, category);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FileOrg] Failed to move: {File}", file);
                result.Errors.Add(Path.GetFileName(file));
            }
        }

        result.Success = true;
        _logger.LogInformation("[FileOrg] Organized {Count} files in {Path}", result.MovedFiles, path);
        return result;
    }

    public Task<List<FileCategory>> GetCategoriesAsync()
    {
        var categories = ExtensionCategories
            .GroupBy(kvp => kvp.Value)
            .Select(g => new FileCategory
            {
                Name = g.Key,
                Extensions = g.Select(kvp => kvp.Key).ToList()
            })
            .ToList();
        return Task.FromResult(categories);
    }

    public string GetCategoryForFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ExtensionCategories.TryGetValue(ext, out var category) ? category : "Autres";
    }
}

public sealed class OrganizationResult
{
    public bool Success { get; set; }
    public string SourcePath { get; set; } = "";
    public int TotalFiles { get; set; }
    public int MovedFiles { get; set; }
    public Dictionary<string, int> FilesByCategory { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public sealed class FileCategory
{
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();
}
