using JarvisAI.Domain.Security;

namespace JarvisAI.Application.Security;

/// <summary>
/// Classifies the risk and destructiveness of each computer-use action. The
/// whole tools are High risk, but actions that physically drive the mouse,
/// keyboard, clipboard or that close windows are considered destructive and
/// always require explicit user confirmation - even in developer mode.
/// </summary>
public static class ComputerUseRiskClassifier
{
    private const string ComputerUseToolName = "computer_use";

    private static readonly HashSet<string> DestructiveActions = new(StringComparer.OrdinalIgnoreCase)
    {
        // clicks and typing drive the real mouse and keyboard.
        "click_element",
        "double_click_element",
        "type_into",
        "press_key",
    };

    public static bool IsComputerTool(string toolName)
        => toolName.Equals(ComputerUseToolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the action physically interacts with the system in a way that
    /// cannot be undone and must never be executed without explicit user approval.
    /// </summary>
    public static bool IsDestructiveAction(string toolName, string? action)
        => IsComputerTool(toolName) && !string.IsNullOrWhiteSpace(action) && DestructiveActions.Contains(action);

    /// <summary>
    /// Per-action risk level for computer tools. Unknown or missing actions keep
    /// the tool-level High risk so they can never be silently downgraded.
    /// </summary>
    public static SecurityRiskLevel GetActionRiskLevel(string toolName, string? action)
    {
        if (!IsComputerTool(toolName) || string.IsNullOrWhiteSpace(action))
            return SecurityRiskLevel.High;

        return action switch
        {
            "observe" or "find_element" => SecurityRiskLevel.Low,
            _ => SecurityRiskLevel.High,
        };
    }
}
