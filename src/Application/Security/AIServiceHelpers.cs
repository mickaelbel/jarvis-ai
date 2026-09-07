using JarvisAI.Application.AI;

namespace JarvisAI.Application.Security;

/// <summary>
/// Méthodes pures (statiques, sans état) extraites de AIServiceAdapter pour
/// réduire la taille de la classe et faciliter le test unitaire.
/// </summary>
internal static class AIServiceHelpers
{
    public static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    public static bool IsToolLooping(Dictionary<string, int> toolCallCounts, IReadOnlyList<AIToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0) return false;
        foreach (var call in toolCalls)
        {
            var args = call.Arguments.Count > 0
                ? string.Join("|", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))
                : "(none)";
            var signature = $"{call.Name}({args})";
            toolCallCounts[signature] = toolCallCounts.GetValueOrDefault(signature) + 1;
            if (toolCallCounts[signature] >= 3)
                return true;

            // Normalized: same tool + same arg keys = loop even with different values
            if (call.Arguments.Count > 0)
            {
                var keyOnlySignature = $"{call.Name}({string.Join("|", call.Arguments.Keys.OrderBy(k => k))})";
                var normKey = $"norm:{keyOnlySignature}";
                toolCallCounts[normKey] = toolCallCounts.GetValueOrDefault(normKey) + 1;
                if (toolCallCounts[normKey] >= 5)
                    return true;
            }
        }
        return false;
    }

    public static bool IsClassificationJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;
        return trimmed.Contains("\"category\"") && (trimmed.Contains("\"multi_step\"") || trimmed.Contains("\"reason\""));
    }

    public static bool IsTransientToolError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        return error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("429", StringComparison.OrdinalIgnoreCase)
            || error.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase)
            || error.Contains("503", StringComparison.OrdinalIgnoreCase)
            || error.Contains("temporairement", StringComparison.OrdinalIgnoreCase);
    }
}
