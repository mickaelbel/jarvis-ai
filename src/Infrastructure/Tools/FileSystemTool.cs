using JarvisAI.Application.Agents;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JarvisAI.Infrastructure.Tools;

public sealed class FileSystemTool : ITool
{
    private readonly ILogger<FileSystemTool> _logger;
    private readonly ISecurityManager? _security;

    public string Name => "file_system";
    public string Description => "Create, read, write, edit, delete, move, copy, and search files and directories on the user's computer. Use this tool whenever the user asks you to do anything with files: create a file, save content, read a file, edit a file, delete a file, create a folder, move/copy files, search for files, list directory contents, or get file info. Supports absolute Windows paths. For creating files: use action=create_file. For writing to existing files: use action=write_file. For targeted edits to existing files: use action=edit_file with 'find' (exact text to locate) and 'replace' (text to substitute). For reading: use action=read_file.";
    public string Category => "filesystem";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: read_file, write_file, create_file, edit_file, delete_file, create_directory, move_file, copy_file, search_files, list_directory, get_file_info", typeof(string), required: true),
        new ToolParameter("path", "File or directory path", typeof(string), required: true),
        new ToolParameter("content", "File content (for write_file/create_file)", typeof(string)),
        new ToolParameter("find", "Exact text to locate (for edit_file) — quote the EXACT line including leading spaces. If it fails, re-read the file and retry with the corrected exact text.", typeof(string)),
        new ToolParameter("replace", "Replacement text (for edit_file)", typeof(string)),
        new ToolParameter("destination", "Destination path (for move_file/copy_file)", typeof(string)),
        new ToolParameter("pattern", "Search pattern (for search_files, e.g. *.txt, *.*)", typeof(string)),
        new ToolParameter("recursive", "Search recursively (true/false, for search_files)", typeof(string)),
    };

    public FileSystemTool(ILogger<FileSystemTool> logger, ISecurityManager? securityManager = null)
    {
        _logger = logger;
        _security = securityManager;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("path", out var path);
        parameters.TryGetValue("content", out var content);
        parameters.TryGetValue("destination", out var dest);
        parameters.TryGetValue("pattern", out var pattern);

        path = NormalizePath(UserPaths.ResolveUserPath(path));
        dest = NormalizePath(UserPaths.ResolveUserPath(dest));

        var actionLower = action?.ToLowerInvariant();

        // The sandbox applies to every operation: reads can leak data and writes
        // can corrupt data, so both directions are checked. A failed normalization
        // (invalid characters, inaccessible path) is treated as denied.
        if (!string.IsNullOrWhiteSpace(path) && _security is not null && !_security.IsPathAllowed(path))
            return ToolResult.Failed($"Access denied: path '{path}' is outside allowed directories");

        if (!string.IsNullOrWhiteSpace(dest) && _security is not null && !_security.IsPathAllowed(dest))
            return ToolResult.Failed($"Access denied: destination path '{dest}' is outside allowed directories");

        if (string.IsNullOrWhiteSpace(path) && actionLower != "search_files")
            return ToolResult.Failed("Parameter 'path' is required");

        try
        {
            return actionLower switch
            {
                "read_file" => await ReadFileAsync(path!, cancellationToken),
                "write_file" => await WriteFileAsync(path!, content, cancellationToken),
                "create_file" => await CreateFileAsync(path!, content, cancellationToken),
                "edit_file" => await EditFileAsync(path!, parameters.TryGetValue("find", out var find) ? find : null, parameters.TryGetValue("replace", out var replace) ? replace : null, cancellationToken),
                "delete_file" => await DeleteFileAsync(path!, cancellationToken),
                "create_directory" => await CreateDirectoryAsync(path!, cancellationToken),
                "move_file" => await MoveFileAsync(path!, dest, cancellationToken),
                "copy_file" => await CopyFileAsync(path!, dest, cancellationToken),
                "search_files" => await SearchFilesAsync(pattern, path, cancellationToken),
                "list_directory" => await ListDirectoryAsync(path!, cancellationToken),
                "get_file_info" => await GetFileInfoAsync(path!, cancellationToken),
                _ => ToolResult.Failed($"Unknown action: {action}. Valid: read_file, write_file, create_file, edit_file, delete_file, create_directory, move_file, copy_file, search_files, list_directory, get_file_info")
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException)
        {
            _logger.LogError(ex, "[FileSystemTool] Action {Action} failed on {Path}", action, path);
            return ToolResult.Failed($"File system error: {ex.Message}");
        }
    }

    private Task<ToolResult> ReadFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Failed($"File not found: {path}"));

        var content = File.ReadAllText(path, Encoding.UTF8);
        _logger.LogInformation("[FileSystemTool] Read file: {Path} ({Length} chars)", path, content.Length);
        return Task.FromResult(ToolResult.Succeeded(content));
    }

    private Task<ToolResult> WriteFileAsync(string path, string? content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, content ?? string.Empty, Encoding.UTF8);
        _logger.LogInformation("[FileSystemTool] Wrote file: {Path} ({Length} chars)", path, (content ?? "").Length);
        return Task.FromResult(ToolResult.Succeeded($"File written: {path} ({new FileInfo(path).Length} bytes)"));
    }

    private Task<ToolResult> CreateFileAsync(string path, string? content, CancellationToken ct)
    {
        if (File.Exists(path))
            return Task.FromResult(ToolResult.Failed($"File already exists: {path}"));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, content ?? string.Empty, Encoding.UTF8);
        _logger.LogInformation("[FileSystemTool] Created file: {Path}", path);
        return Task.FromResult(ToolResult.Succeeded($"File created: {path}"));
    }

    private Task<ToolResult> EditFileAsync(string path, string? find, string? replace, CancellationToken ct)
    {
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Failed($"File not found: {path}"));

        if (string.IsNullOrWhiteSpace(find))
            return Task.FromResult(ToolResult.Failed("Parameter 'find' is required for edit_file"));

        var original = File.ReadAllText(path, Encoding.UTF8);
        var occurrence = original.IndexOf(find, StringComparison.Ordinal);
        if (occurrence < 0)
        {
            var insensitive = original.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (insensitive >= 0)
                return Task.FromResult(ToolResult.Failed(
                    $"Text not found with exact casing, but it exists with a different casing at line {LineAt(original, insensitive)}. Re-read the file and retry with the EXACT text."));

            if (FindNearestLine(original, find, out var nearestLine, out var nearestText) is { } near)
                return Task.FromResult(ToolResult.Failed(
                    $"Text not found. Closest line (line {nearestLine}): \"{near}\". Re-read the file and retry with the exact line."));

            return Task.FromResult(ToolResult.Failed("Text to find was not found in the file"));
        }

        var updated = original.Remove(occurrence, find.Length)
            .Insert(occurrence, replace ?? string.Empty);
        File.WriteAllText(path, updated, Encoding.UTF8);
        _logger.LogInformation("[FileSystemTool] Edited file: {Path}", path);
        return Task.FromResult(ToolResult.Succeeded($"File edited: {path} (line {LineAt(original, occurrence)}, replaced {find.Length} chars)"));
    }

    private static int LineAt(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string? FindNearestLine(string text, string needle, out int lineNumber, out string nearestText)
    {
        lineNumber = -1;
        nearestText = "";
        var lines = text.Split('\n');
        var firstLine = needle.Split('\n')[0].Trim();
        var bestScore = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var candidate = lines[i].Trim().TrimEnd('\r');
            if (candidate.Length == 0) continue;
            var score = 0;
            var max = Math.Min(candidate.Length, firstLine.Length);
            for (var j = 0; j < max; j++)
            {
                if (candidate[j] == firstLine[j]) score++;
                else break;
            }
            if (score > bestScore)
            {
                bestScore = score;
                lineNumber = i + 1;
                nearestText = candidate;
            }
        }
        return bestScore >= Math.Max(3, firstLine.Length / 2) ? nearestText : null;
    }

    private Task<ToolResult> DeleteFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Failed($"File not found: {path}"));

        File.Delete(path);
        _logger.LogInformation("[FileSystemTool] Deleted file: {Path}", path);
        return Task.FromResult(ToolResult.Succeeded($"File deleted: {path}"));
    }

    private Task<ToolResult> CreateDirectoryAsync(string path, CancellationToken ct)
    {
        if (Directory.Exists(path))
            return Task.FromResult(ToolResult.Failed($"Directory already exists: {path}"));

        Directory.CreateDirectory(path);
        _logger.LogInformation("[FileSystemTool] Created directory: {Path}", path);
        return Task.FromResult(ToolResult.Succeeded($"Directory created: {path}"));
    }

    private Task<ToolResult> MoveFileAsync(string source, string? dest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dest))
            return Task.FromResult(ToolResult.Failed("Parameter 'destination' is required for move_file"));

        if (!File.Exists(source))
            return Task.FromResult(ToolResult.Failed($"Source file not found: {source}"));

        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        File.Move(source, dest, overwrite: true);
        _logger.LogInformation("[FileSystemTool] Moved file: {Source} -> {Dest}", source, dest);
        return Task.FromResult(ToolResult.Succeeded($"File moved: {source} -> {dest}"));
    }

    private Task<ToolResult> CopyFileAsync(string source, string? dest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dest))
            return Task.FromResult(ToolResult.Failed("Parameter 'destination' is required for copy_file"));

        if (!File.Exists(source))
            return Task.FromResult(ToolResult.Failed($"Source file not found: {source}"));

        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        File.Copy(source, dest, overwrite: true);
        _logger.LogInformation("[FileSystemTool] Copied file: {Source} -> {Dest}", source, dest);
        return Task.FromResult(ToolResult.Succeeded($"File copied: {source} -> {dest}"));
    }

    private Task<ToolResult> SearchFilesAsync(string? pattern, string? searchPath, CancellationToken ct)
    {
        var searchPattern = pattern ?? "*.*";
        var root = searchPath ?? Directory.GetCurrentDirectory();
        var recursive = true;

        if (!Directory.Exists(root))
            return Task.FromResult(ToolResult.Failed($"Directory not found: {root}"));

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(root, searchPattern, option)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(200)
            .ToList();

        if (files.Count == 0)
            return Task.FromResult(ToolResult.Succeeded($"No files matching '{searchPattern}' in {root}"));

        var sb = new StringBuilder();
        sb.AppendLine($"Found {files.Count} file(s) matching '{searchPattern}' in {root}:");
        foreach (var f in files)
        {
            sb.AppendLine($"  {f.FullName} ({FormatSize(f.Length)}, modified {f.LastWriteTime:yyyy-MM-dd HH:mm})");
        }
        return Task.FromResult(ToolResult.Succeeded(sb.ToString()));
    }

    private Task<ToolResult> ListDirectoryAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Failed($"Directory not found: {path}"));

        var dirs = Directory.GetDirectories(path);
        var files = Directory.GetFiles(path);

        var sb = new StringBuilder();
        sb.AppendLine($"Directory: {path}");
        sb.AppendLine($"  Subdirectories ({dirs.Length}):");
        foreach (var d in dirs.Take(50))
            sb.AppendLine($"    {Path.GetFileName(d)}/");
        if (dirs.Length > 50) sb.AppendLine($"    ... and {dirs.Length - 50} more");

        sb.AppendLine($"  Files ({files.Length}):");
        foreach (var f in files.Take(100))
        {
            var fi = new FileInfo(f);
            sb.AppendLine($"    {fi.Name} ({FormatSize(fi.Length)})");
        }
        if (files.Length > 100) sb.AppendLine($"    ... and {files.Length - 100} more");

        return Task.FromResult(ToolResult.Succeeded(sb.ToString()));
    }

    private Task<ToolResult> GetFileInfoAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            var info = new
            {
                Name = fi.Name,
                FullPath = fi.FullName,
                Size = fi.Length,
                SizeDisplay = FormatSize(fi.Length),
                Created = fi.CreationTime,
                Modified = fi.LastWriteTime,
                Attributes = fi.Attributes.ToString(),
                IsReadOnly = fi.IsReadOnly,
                Directory = fi.DirectoryName
            };
            return Task.FromResult(ToolResult.Succeeded(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
        }

        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            var info = new
            {
                Name = di.Name,
                FullPath = di.FullName,
                Created = di.CreationTime,
                Modified = di.LastWriteTime,
                Parent = di.Parent?.FullName,
                FileCount = di.GetFiles().Length,
                DirectoryCount = di.GetDirectories().Length
            };
            return Task.FromResult(ToolResult.Succeeded(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
        }

        return Task.FromResult(ToolResult.Failed($"Path not found: {path}"));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            // Invalid characters or inaccessible roots: fail closed.
            return null;
        }
    }
}
