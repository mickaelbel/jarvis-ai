namespace JarvisAI.Application.Plugins;

/// <summary>
/// Maximum permission a plugin can exercise. A plugin can never register a tool
/// whose risk level exceeds its declared permission level.
/// </summary>
public enum PluginPermissionLevel
{
    /// <summary>Read-only and informational tools only (Low risk).</summary>
    Low = 0,

    /// <summary>Low and Medium risk tools (files, processes, clipboard...).</summary>
    Medium = 1,

    /// <summary>Any tool, including High risk (terminal, system control...).</summary>
    High = 2
}
