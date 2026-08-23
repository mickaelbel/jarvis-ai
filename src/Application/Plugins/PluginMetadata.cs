namespace JarvisAI.Application.Plugins;

public sealed class PluginMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string AssemblyPath { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = new();
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// Maximum permission the plugin is granted. It can never register a tool
    /// whose risk level exceeds this level.
    /// </summary>
    public PluginPermissionLevel PermissionLevel { get; set; } = PluginPermissionLevel.Low;

    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
    public PluginState State { get; set; } = PluginState.Discovered;
}
