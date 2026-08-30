namespace JarvisAI.Application.Configuration;

/// <summary>
/// Export/Import de configuration Jarvis AI.
/// </summary>
public interface IConfigExportService
{
    Task<string> ExportAsync(ConfigExportOptions options, CancellationToken ct = default);
    Task<ConfigImportResult> ImportAsync(string json, CancellationToken ct = default);
    Task<ConfigExportOptions> GetDefaultOptionsAsync();
}

public sealed record ConfigExportOptions(
    bool IncludeSettings = true,
    bool IncludeMemory = false,
    bool IncludeEpisodicMemory = false,
    bool IncludeTools = true,
    bool IncludePermissions = false);

public sealed record ConfigImportResult(
    bool Success,
    int SettingsImported,
    int MemoryImported,
    int ToolsImported,
    string? Error);
