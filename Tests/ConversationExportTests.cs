using System.Text.Json;
using JarvisAI.Web.Services;

namespace JarvisAI.Tests;

/// <summary>
/// Unit tests for <see cref="ConversationExportService"/>: plain text, Markdown
/// and JSON exports of a chat session with the metadata captured by the UI.
/// </summary>
public class ConversationExportTests
{
    private static ConversationExportEntry Entry(string role, string content, string? model = null, int toolCalls = 0, int? tokens = null)
        => new()
        {
            Role = role,
            Content = content,
            Timestamp = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
            Model = model,
            ToolCallCount = toolCalls,
            DurationMs = 1234,
            TokenCount = tokens
        };

    private static IReadOnlyList<ConversationExportEntry> SampleConversation() => new[]
    {
        Entry("user", "What is 2+2?"),
        Entry("assistant", "4", model: "llama3.1", toolCalls: 1, tokens: 42)
    };

    [Fact]
    public void Export_txt_contains_messages_and_metadata()
    {
        var service = new ConversationExportService();

        var content = service.Export(SampleConversation(), ConversationExportFormat.Txt);

        Assert.Contains("[User]", content);
        Assert.Contains("What is 2+2?", content);
        Assert.Contains("[Assistant]", content);
        Assert.Contains("model: llama3.1", content);
        Assert.Contains("tool calls: 1", content);
        Assert.Contains("tokens: 42", content);
        Assert.Contains("generation: 1234 ms", content);
    }

    [Fact]
    public void Export_markdown_contains_headers_and_metadata()
    {
        var service = new ConversationExportService();

        var content = service.Export(SampleConversation(), ConversationExportFormat.Markdown);

        Assert.Contains("## User", content);
        Assert.Contains("## Assistant", content);
        Assert.Contains("**llama3.1**", content);
        Assert.Contains("42 tokens", content);
        Assert.Contains("1 tool call(s)", content);
    }

    [Fact]
    public void Export_json_is_valid_and_contains_roles()
    {
        var service = new ConversationExportService();

        var json = service.Export(SampleConversation(), ConversationExportFormat.Json);
        using var doc = JsonDocument.Parse(json);

        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("messageCount").GetInt32());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("llama3.1", messages[1].GetProperty("model").GetString());
        Assert.Equal(42, messages[1].GetProperty("tokenCount").GetInt32());
    }

    [Fact]
    public void Export_json_reports_timestamp_for_each_message()
    {
        var service = new ConversationExportService();

        var json = service.Export(SampleConversation(), ConversationExportFormat.Json);
        using var doc = JsonDocument.Parse(json);

        var first = doc.RootElement.GetProperty("messages")[0];
        Assert.True(first.TryGetProperty("timestamp", out _));
        Assert.False(first.GetProperty("timestamp").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void Empty_conversation_exports_cleanly()
    {
        var service = new ConversationExportService();

        var txt = service.Export(Array.Empty<ConversationExportEntry>(), ConversationExportFormat.Txt);
        var json = service.Export(Array.Empty<ConversationExportEntry>(), ConversationExportFormat.Json);

        Assert.Contains("Messages: 0", txt);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("messageCount").GetInt32());
    }

    [Fact]
    public void BuildDownload_returns_utf8_bytes()
    {
        var service = new ConversationExportService();

        var bytes = service.BuildDownload("héllo");

        Assert.Equal("héllo", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void FileName_matches_extension_per_format()
    {
        Assert.EndsWith(".txt", ConversationExportService.FileName(ConversationExportFormat.Txt));
        Assert.EndsWith(".md", ConversationExportService.FileName(ConversationExportFormat.Markdown));
        Assert.EndsWith(".json", ConversationExportService.FileName(ConversationExportFormat.Json));
    }

    [Fact]
    public void ContentType_is_correct_per_format()
    {
        Assert.Equal("text/plain", ConversationExportService.ContentType(ConversationExportFormat.Txt));
        Assert.Equal("text/markdown", ConversationExportService.ContentType(ConversationExportFormat.Markdown));
        Assert.Equal("application/json", ConversationExportService.ContentType(ConversationExportFormat.Json));
    }
}
