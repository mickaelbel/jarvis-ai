using JarvisAI.Application.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Configuration;

public sealed class ConfigExportService : IConfigExportService
{
    private readonly ILogger<ConfigExportService> _logger;
    private readonly string _configDir;

    public ConfigExportService(ILogger<ConfigExportService> logger)
    {
        _logger = logger;
        _configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI");
        Directory.CreateDirectory(_configDir);
    }

    public async Task<string> ExportAsync(ConfigExportOptions options, CancellationToken ct = default)
    {
        var export = new Dictionary<string, object>();

        if (options.IncludeSettings)
        {
            var settingsPath = Path.Combine(_configDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                var settings = await File.ReadAllTextAsync(settingsPath, ct);
                export["settings"] = JsonSerializer.Deserialize<object>(settings);
            }
        }

        if (options.IncludeMemory)
        {
            var memoryPath = Path.Combine(_configDir, "memory.json");
            if (File.Exists(memoryPath))
            {
                var memory = await File.ReadAllTextAsync(memoryPath, ct);
                export["memory"] = JsonSerializer.Deserialize<object>(memory);
            }
        }

        if (options.IncludeTools)
        {
            var toolsDir = Path.Combine(_configDir, "tools");
            if (Directory.Exists(toolsDir))
            {
                var toolConfigs = new Dictionary<string, object>();
                foreach (var file in Directory.GetFiles(toolsDir, "*.json"))
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    toolConfigs[Path.GetFileNameWithoutExtension(file)] = JsonSerializer.Deserialize<object>(content);
                }
                export["tools"] = toolConfigs;
            }
        }

        if (options.IncludePermissions)
        {
            var permsPath = Path.Combine(_configDir, "permissions.json");
            if (File.Exists(permsPath))
            {
                var perms = await File.ReadAllTextAsync(permsPath, ct);
                export["permissions"] = JsonSerializer.Deserialize<object>(perms);
            }
        }

        export["exportDate"] = DateTime.Now.ToString("o");
        export["version"] = "1.0";

        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<ConfigImportResult> ImportAsync(string json, CancellationToken ct = default)
    {
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data is null)
                return new ConfigImportResult(false, 0, 0, 0, "Invalid JSON");

            int settingsImported = 0, memoryImported = 0, toolsImported = 0;

            if (data.ContainsKey("settings"))
            {
                var settingsPath = Path.Combine(_configDir, "settings.json");
                var settingsJson = JsonSerializer.Serialize(data["settings"], new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(settingsPath, settingsJson, ct);
                settingsImported = 1;
            }

            if (data.ContainsKey("memory"))
            {
                var memoryPath = Path.Combine(_configDir, "memory.json");
                var memoryJson = JsonSerializer.Serialize(data["memory"], new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(memoryPath, memoryJson, ct);
                memoryImported = 1;
            }

            if (data.ContainsKey("tools"))
            {
                var toolsDir = Path.Combine(_configDir, "tools");
                Directory.CreateDirectory(toolsDir);
                var toolsObj = data["tools"];
                foreach (var prop in toolsObj.EnumerateObject())
                {
                    var toolPath = Path.Combine(toolsDir, $"{prop.Name}.json");
                    var toolJson = JsonSerializer.Serialize(prop.Value, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(toolPath, toolJson, ct);
                    toolsImported++;
                }
            }

            _logger.LogInformation("[ConfigExport] Imported: {Settings} settings, {Memory} memory, {Tools} tools",
                settingsImported, memoryImported, toolsImported);

            return new ConfigImportResult(true, settingsImported, memoryImported, toolsImported, null);
        }
        catch (Exception ex)
        {
            return new ConfigImportResult(false, 0, 0, 0, ex.Message);
        }
    }

    public Task<ConfigExportOptions> GetDefaultOptionsAsync()
    {
        return Task.FromResult(new ConfigExportOptions());
    }
}
