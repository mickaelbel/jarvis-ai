using JarvisAI.Application.Plugins;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Application.Security;

/// <summary>
/// Static catalog of known plugin permission names and the minimum risk level
/// they authorize. Unknown permissions are treated as High risk (fail closed)
/// so a plugin can never quietly declare an unrecognized, dangerous capability.
/// </summary>
public static class PluginPermissionCatalog
{
    public static readonly IReadOnlyDictionary<string, SecurityRiskLevel> KnownPermissions =
        new Dictionary<string, SecurityRiskLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["system_info"] = SecurityRiskLevel.Low,
            ["read_only"] = SecurityRiskLevel.Low,
            ["file_read"] = SecurityRiskLevel.Low,
            ["memory_read"] = SecurityRiskLevel.Low,
            ["time"] = SecurityRiskLevel.Low,

            ["process_list"] = SecurityRiskLevel.Medium,
            ["file_write"] = SecurityRiskLevel.Medium,
            ["network"] = SecurityRiskLevel.Medium,
            ["audio"] = SecurityRiskLevel.Medium,
            ["clipboard"] = SecurityRiskLevel.Medium,

            ["system_control"] = SecurityRiskLevel.High,
            ["terminal"] = SecurityRiskLevel.High,
            ["file_delete"] = SecurityRiskLevel.High,
            ["shutdown"] = SecurityRiskLevel.High,
            ["registry"] = SecurityRiskLevel.High,
            ["screen_capture"] = SecurityRiskLevel.High
        };

    public static bool IsKnown(string permission)
        => KnownPermissions.ContainsKey(permission);

    public static SecurityRiskLevel GetRequiredRisk(string permission)
        => KnownPermissions.TryGetValue(permission, out var risk) ? risk : SecurityRiskLevel.High;
}

/// <summary>
/// Enforces the plugin security model. A plugin declares its permissions and a
/// maximum permission level; it can only register tools whose risk does not
/// exceed that level. The catalog is used to validate declared permission names.
/// </summary>
public sealed class PluginPermissionManager
{
    /// <summary>
    /// Returns the list of declared permission names that are unknown to the
    /// catalog. An empty result means every declared permission is recognized.
    /// </summary>
    public IReadOnlyList<string> GetUnknownPermissions(PluginMetadata metadata)
    {
        return metadata.Permissions
            .Where(p => !PluginPermissionCatalog.IsKnown(p))
            .ToList();
    }

    /// <summary>
    /// True when the plugin's declared permission level authorizes a tool with
    /// the given risk level.
    /// </summary>
    public bool CanRegisterTool(PluginMetadata metadata, ITool tool)
        => IsRiskAllowed(tool.RiskLevel, metadata.PermissionLevel);

    /// <summary>
    /// True when a tool with <paramref name="toolRisk"/> is within the range
    /// authorized by <paramref name="pluginLevel"/>.
    /// </summary>
    public static bool IsRiskAllowed(SecurityRiskLevel toolRisk, PluginPermissionLevel pluginLevel)
    {
        return pluginLevel switch
        {
            PluginPermissionLevel.Low => toolRisk <= SecurityRiskLevel.Low,
            PluginPermissionLevel.Medium => toolRisk <= SecurityRiskLevel.Medium,
            _ => true
        };
    }

    /// <summary>
    /// The highest risk level a plugin is allowed to register, derived from its
    /// declared permission level.
    /// </summary>
    public static SecurityRiskLevel MaxAllowedRisk(PluginPermissionLevel pluginLevel)
        => pluginLevel switch
        {
            PluginPermissionLevel.Low => SecurityRiskLevel.Low,
            PluginPermissionLevel.Medium => SecurityRiskLevel.Medium,
            _ => SecurityRiskLevel.High
        };
}
