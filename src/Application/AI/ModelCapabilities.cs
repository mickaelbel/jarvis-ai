namespace JarvisAI.Application.AI;

public static class ModelCapabilities
{
    private static readonly string[] NoToolSupportMarkers =
    {
        "llava",
        "bakllava",
        "moondream",
        "minicpm",
        "phi3-vision",
        "phi-3-vision",
        "nomic-embed-text",
    };

    public static bool SupportsTools(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true;
        foreach (var marker in NoToolSupportMarkers)
        {
            if (model.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
