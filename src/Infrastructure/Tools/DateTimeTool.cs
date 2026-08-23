using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Infrastructure.Tools;

public sealed class DateTimeTool : ITool
{
    public string Name => "date_time";
    public string Description => "Returns the current date and time in UTC and local timezone";
    public string Category => "system";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public bool McpExpose => true;

    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var utcNow = DateTime.UtcNow;

        var info = new
        {
            UtcNow = utcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            LocalNow = now.ToString("yyyy-MM-dd HH:mm:ss"),
            Timezone = TimeZoneInfo.Local.DisplayName,
            DayOfWeek = now.DayOfWeek.ToString()
        };

        return Task.FromResult(ToolResult.Succeeded(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }
}

