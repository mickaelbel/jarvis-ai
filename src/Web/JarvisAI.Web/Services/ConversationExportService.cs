using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

/// <summary>Supported conversation export formats.</summary>
public enum ConversationExportFormat
{
    Txt,
    Markdown,
    Json
}

/// <summary>
/// A single message (user or assistant) with the metadata captured during a
/// chat session: timestamp, model, tool calls and agent duration.
/// </summary>
public sealed class ConversationExportEntry
{
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? Model { get; init; }
    public int ToolCallCount { get; init; }
    public double? DurationMs { get; init; }
    public double? AgentDurationMs { get; init; }
    public int? TokenCount { get; init; }
}

/// <summary>
/// Serializes a conversation to plain text, Markdown or JSON for download.
/// Kept free of any Blazor dependency so it is trivially unit-testable.
/// </summary>
public sealed class ConversationExportService
{
    public string Export(IReadOnlyList<ConversationExportEntry> messages, ConversationExportFormat format)
    {
        return format switch
        {
            ConversationExportFormat.Txt => ExportTxt(messages),
            ConversationExportFormat.Markdown => ExportMarkdown(messages),
            _ => ExportJson(messages)
        };
    }

    public string ExportJson(IReadOnlyList<ConversationExportEntry> messages)
    {
        var payload = new
        {
            exportedAt = DateTimeOffset.UtcNow,
            messageCount = messages.Count,
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                timestamp = m.Timestamp,
                model = m.Model,
                toolCalls = m.ToolCallCount,
                durationMs = m.DurationMs,
                agentDurationMs = m.AgentDurationMs,
                tokenCount = m.TokenCount
            })
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public string ExportTxt(IReadOnlyList<ConversationExportEntry> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Jarvis AI — Conversation export");
        sb.AppendLine($"Exported: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}");
        sb.AppendLine($"Messages: {messages.Count}");
        sb.AppendLine(new string('=', 60));

        foreach (var m in messages)
        {
            var label = string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
            sb.AppendLine();
            sb.AppendLine($"[{label}] {m.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            if (!string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                var details = new List<string>();
                if (!string.IsNullOrEmpty(m.Model)) details.Add($"model: {m.Model}");
                if (m.TokenCount.HasValue) details.Add($"tokens: {m.TokenCount.Value}");
                if (m.ToolCallCount > 0) details.Add($"tool calls: {m.ToolCallCount}");
                if (m.DurationMs.HasValue) details.Add($"generation: {m.DurationMs.Value:F0} ms");
                if (m.AgentDurationMs.HasValue) details.Add($"agent total: {m.AgentDurationMs.Value:F0} ms");
                if (details.Count > 0) sb.AppendLine($"      ({string.Join(" · ", details)})");
            }
            sb.AppendLine(m.Content);
        }
        return sb.ToString();
    }

    public string ExportMarkdown(IReadOnlyList<ConversationExportEntry> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Jarvis AI — Conversation export");
        sb.AppendLine();
        sb.AppendLine($"Exported: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'} · Messages: {messages.Count}");
        sb.AppendLine();

        foreach (var m in messages)
        {
            if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## User");
                sb.AppendLine();
                sb.AppendLine(m.Content);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## Assistant");
                sb.AppendLine();
                var chips = new List<string>();
                if (!string.IsNullOrEmpty(m.Model)) chips.Add($"**{m.Model}**");
                if (m.TokenCount.HasValue) chips.Add($"{m.TokenCount.Value} tokens");
                if (m.ToolCallCount > 0) chips.Add($"{m.ToolCallCount} tool call(s)");
                if (m.DurationMs.HasValue) chips.Add($"{m.DurationMs.Value:F0} ms");
                if (m.AgentDurationMs.HasValue) chips.Add($"agent {m.AgentDurationMs.Value:F0} ms");
                sb.AppendLine($"> {string.Join(" · ", chips)}");
                sb.AppendLine();
                sb.AppendLine(m.Content);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public byte[] BuildDownload(string content)
        => Encoding.UTF8.GetBytes(content);

    public static string FileName(ConversationExportFormat format)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return format switch
        {
            ConversationExportFormat.Txt => $"jarvis-conversation_{stamp}.txt",
            ConversationExportFormat.Markdown => $"jarvis-conversation_{stamp}.md",
            _ => $"jarvis-conversation_{stamp}.json"
        };
    }

    public static string ContentType(ConversationExportFormat format)
        => format switch
        {
            ConversationExportFormat.Txt => "text/plain",
            ConversationExportFormat.Markdown => "text/markdown",
            _ => "application/json"
        };
}
