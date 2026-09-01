namespace JarvisAI.Application.Plugins;

public interface IExternalPluginManager
{
    Task<IReadOnlyList<ExternalPlugin>> GetPluginsAsync();
    Task<ExternalPlugin?> GetPluginAsync(string pluginId);
    Task<bool> InstallAsync(string pluginId, string targetVersion, CancellationToken ct = default);
    Task<bool> UninstallAsync(string pluginId, CancellationToken ct = default);
    Task<bool> UpdateAsync(string pluginId, CancellationToken ct = default);
    Task<ExternalPluginStatus> GetStatusAsync(string pluginId);
    Task<List<string>> DetectTargetVersionsAsync(string pluginId);
}
