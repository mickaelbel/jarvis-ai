namespace JarvisAI.Application.ComputerUse;

public enum UiElementType
{
    Button,
    Input,
    Link,
    Text,
    Icon,
    Image,
    Region,
    Dropdown,
    Checkbox
}

public sealed record UiElement(
    int Id,
    UiElementType Type,
    string Label,
    int X,
    int Y,
    int Width,
    int Height,
    int CenterX,
    int CenterY,
    double Confidence);

public interface IUiElementDetector
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<UiElement>> DetectAsync(byte[] imageBytes, int screenWidth, int screenHeight, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fuzzy label matching used by intelligent click: the LLM asks for "the OK button",
/// the matcher finds the closest detected element by normalized label similarity.
/// </summary>
public static class UiElementMatcher
{
    public static UiElement? BestMatch(IReadOnlyList<UiElement> elements, string query)
    {
        if (elements is null || elements.Count == 0 || string.IsNullOrWhiteSpace(query))
            return null;

        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
            return null;

        UiElement? best = null;
        var bestScore = double.MinValue;

        foreach (var element in elements)
        {
            var label = Normalize(element.Label);
            if (label.Length == 0)
                continue;

            var score = Score(label, normalizedQuery);

            if (score > bestScore)
            {
                bestScore = score;
                best = element;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static double Score(string label, string query)
    {
        // Exact whole-label match is the strongest signal.
        if (label == query)
            return 1.0;

        // A short element label that appears inside the query (e.g. "send" in "send message").
        if (query.Contains(label, StringComparison.Ordinal) && label.Length >= 2)
            return 0.9 - 0.05 * label.Length;

        if (label.Contains(query, StringComparison.Ordinal))
            return 0.85 - 0.02 * Math.Abs(label.Length - query.Length);

        // Prefix agreement.
        if (label.StartsWith(query, StringComparison.Ordinal) && query.Length >= 2)
            return 0.7;

        return 0.0;
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
