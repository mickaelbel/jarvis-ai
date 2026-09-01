namespace JarvisAI.Application.Plugins;

public sealed class ExternalPlugin
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "JarvisAI";
    public string DownloadUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public ExternalPluginType Type { get; set; }
    public ExternalPluginStatus Status { get; set; } = ExternalPluginStatus.NotInstalled;
    public string? InstalledPath { get; set; }
    public string? InstalledVersion { get; set; }
    public string? TargetApplication { get; set; }
    public List<string> SupportedVersions { get; set; } = new();
}

public enum ExternalPluginType
{
    BlenderAddon,
    VSCodeExtension,
    ObsidianPlugin,
    Custom
}

public enum ExternalPluginStatus
{
    NotInstalled,
    Downloading,
    Installing,
    Installed,
    UpdateAvailable,
    Error
}
