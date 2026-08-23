namespace JarvisAI.Application.Tools;

public sealed class ToolTimeoutOptions
{
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public Dictionary<string, TimeSpan> PerToolTimeouts { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["terminal"] = TimeSpan.FromSeconds(120),
        ["browser"] = TimeSpan.FromSeconds(60),
        ["computer"] = TimeSpan.FromSeconds(30),
        ["computer_use"] = TimeSpan.FromSeconds(30)
    };

    public TimeSpan GetTimeout(string toolName)
    {
        if (PerToolTimeouts.TryGetValue(toolName, out var timeout))
            return timeout;
        return DefaultTimeout;
    }
}