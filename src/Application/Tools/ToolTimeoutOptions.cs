namespace JarvisAI.Application.Tools;

public sealed class ToolTimeoutOptions
{
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public Dictionary<string, TimeSpan> PerToolTimeouts { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["terminal"] = TimeSpan.FromSeconds(120),
        ["browser"] = TimeSpan.FromSeconds(60),
        ["computer"] = TimeSpan.FromSeconds(30),
        ["computer_use"] = TimeSpan.FromSeconds(30),
        ["date_time"] = TimeSpan.FromSeconds(5),
        ["system_info"] = TimeSpan.FromSeconds(5),
        ["calculator"] = TimeSpan.FromSeconds(5),
        ["memory"] = TimeSpan.FromSeconds(10),
        ["clipboard"] = TimeSpan.FromSeconds(5),
        ["ui_elements"] = TimeSpan.FromSeconds(10),
        ["vision"] = TimeSpan.FromSeconds(15),
        ["weather"] = TimeSpan.FromSeconds(10),
        ["news"] = TimeSpan.FromSeconds(10),
        ["web_search"] = TimeSpan.FromSeconds(15),
        ["read_document"] = TimeSpan.FromSeconds(15),
        ["dictation"] = TimeSpan.FromSeconds(10),
        ["reminders"] = TimeSpan.FromSeconds(5),
        ["timer"] = TimeSpan.FromSeconds(5),
        ["routines"] = TimeSpan.FromSeconds(5),
        ["set_voice"] = TimeSpan.FromSeconds(5),
        ["permissions"] = TimeSpan.FromSeconds(5),
        ["budget"] = TimeSpan.FromSeconds(5),
        ["personality"] = TimeSpan.FromSeconds(5)
    };

    public TimeSpan GetTimeout(string toolName)
    {
        if (PerToolTimeouts.TryGetValue(toolName, out var timeout))
            return timeout;
        return DefaultTimeout;
    }
}
