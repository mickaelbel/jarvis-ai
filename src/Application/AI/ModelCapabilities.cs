namespace JarvisAI.Application.AI;

public static class ModelCapabilities
{
    private static readonly object Lock = new();
    private static string[] _noToolSupportMarkers =
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
        var merged = baseMarkers
            .Concat(additionalNoToolModels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (Lock)
        {
            _noToolSupportMarkers = merged;
        }
    }

    public static bool SupportsTools(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true;
        string[] markers;
        lock (Lock)
        {
            markers = _noToolSupportMarkers;
        }
        foreach (var marker in markers)
        {
            // Délimités par ':' ou '/' pour éviter les faux positifs ("not-qwen3.5:2b").
            if (MatchesMarker(model, marker))
                return false;
        }
        return true;
    }

    private static bool MatchesMarker(string model, string marker)
    {
        if (model.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            var idx = model.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var before = idx > 0 ? model[idx - 1] : '\0';
            return before is ':' or '/' or '.' or '\0';
        }
        return false;
    }
}
