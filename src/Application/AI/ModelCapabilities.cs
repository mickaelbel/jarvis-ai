namespace JarvisAI.Application.AI;

public static class ModelCapabilities
{
    private static volatile string[] _noToolSupportMarkers =
    {
        "llava",
        "bakllava",
        "moondream",
        "minicpm",
        "phi3-vision",
        "phi-3-vision",
        "nomic-embed-text",
        "qwen3.5:2b",
        "qwen3.5:0.8b",
        "qwen2.5:1.5b",
        "qwen2.5:3b",
        "gemma2:2b",
        "phi3:3.8b",
        "llama3.2:1b",
        "llama3.2:3b",
        "llama3.1:8b",
    };

    public static void Configure(IEnumerable<string> additionalNoToolModels)
    {
        var baseMarkers = new[]
        {
            "llava", "bakllava", "moondream", "minicpm",
            "phi3-vision", "phi-3-vision", "nomic-embed-text",
        };
        _noToolSupportMarkers = baseMarkers
            .Concat(additionalNoToolModels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool SupportsTools(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true;
        foreach (var marker in _noToolSupportMarkers)
        {
            if (model.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
