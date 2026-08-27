using JarvisAI.Application.AI;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Security;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public class AgentToolCallingTests
{
    private static IMemoryService CreateMemoryService()
    {
        var store = new InMemoryMemoryStore();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new MemoryService(store, eventBus, NullLogger<MemoryService>.Instance);
    }

    private static ToolRegistry CreateFullRegistry(ILogger<ToolRegistry>? logger = null)
    {
        var registry = new ToolRegistry(logger ?? NullLogger<ToolRegistry>.Instance);
        registry.Register(new DateTimeTool());
        registry.Register(new SystemInfoTool());
        registry.Register(new MemoryTool(CreateMemoryService(), new AutomaticMemoryService(CreateMemoryService(), new AutomaticMemoryOptions(), NullLogger<AutomaticMemoryService>.Instance), NullLogger<MemoryTool>.Instance));
        registry.Register(new FileSystemTool(NullLogger<FileSystemTool>.Instance));
        registry.Register(new TerminalTool(NullLogger<TerminalTool>.Instance));
        registry.Register(new ProcessTool(NullLogger<ProcessTool>.Instance));
        registry.Register(new BrowserTool(NullLogger<BrowserTool>.Instance));
        registry.Register(new ClipboardTool(NullLogger<ClipboardTool>.Instance));
        registry.Register(new WindowsTool(NullLogger<WindowsTool>.Instance));
        return registry;
    }

    // ─── TEST 1: File creation triggers tool call ──────────────────────────

    [Fact]
    public async Task Create_file_request_triggers_file_system_tool()
    {
        var registry = CreateFullRegistry();
        var callCount = 0;

        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "file_system", new Dictionary<string, string>
                    {
                        ["action"] = "create_file",
                        ["path"] = @"C:\Users\belmi\Documents\jarvis-ai\test.txt",
                        ["content"] = "hello"
                    })
                });
            }
            return AIResponse.Text("The file has been created successfully at the specified path.");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        var result = await aiService.ChatAsync("Crée un fichier test.txt contenant hello");

        Assert.True(result.Success);
        Assert.DoesNotContain("cannot", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I don't have access", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, callCount);
    }

    // ─── TEST 2: Time request triggers date_time tool ──────────────────────

    [Fact]
    public async Task Time_request_triggers_date_time_tool()
    {
        var registry = CreateFullRegistry();
        var callCount = 0;

        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "date_time", new Dictionary<string, string>())
                });
            }
            return AIResponse.Text("It is 2025-01-15 10:30:00 UTC.");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        var result = await aiService.ChatAsync("Donne-moi l'heure");

        Assert.True(result.Success);
        Assert.Equal("It is 2025-01-15 10:30:00 UTC.", result.Content);
        Assert.Equal(2, callCount);
    }

    // ─── TEST 3: Open browser triggers tool ────────────────────────────────

    [Fact]
    public async Task Open_browser_request_triggers_terminal_or_browser_tool()
    {
        var registry = CreateFullRegistry();
        var callCount = 0;
        string? toolName = null;

        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                toolName = "terminal";
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "terminal", new Dictionary<string, string>
                    {
                        ["action"] = "execute_command",
                        ["command"] = "start chrome"
                    })
                });

            }
            return AIResponse.Text("Chrome has been opened.");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        var result = await aiService.ChatAsync("Ouvre le navigateur");

        Assert.True(result.Success);
        Assert.Equal("terminal", toolName);
        Assert.Equal(2, callCount);
    }

    // ─── TEST 4: Refusal patterns are defined (verified through reflection) ──

    [Fact]
    public void Refusal_detection_patterns_are_defined()
    {
        var type = typeof(AIServiceAdapter);
        var method = type.GetMethod("IsRefusalResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, new object[] { "I cannot access your files" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "I can't create files" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "Je ne peux pas accéder" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "Here are the PowerShell commands" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "You can run this command" })!);

        Assert.False((bool)method.Invoke(null, new object[] { "The file has been created" })!);
        Assert.False((bool)method.Invoke(null, new object[] { "I used the file_system tool" })!);
        Assert.False((bool)method.Invoke(null, new object[] { "Salut ! Comment puis-je vous aider aujourd'hui ?" })!);
        Assert.False((bool)method.Invoke(null, new object[] { "Bonjour, je suis Jarvis. Que puis-je faire pour vous ?" })!);
        Assert.False((bool)method.Invoke(null, new object[] { "Bien sûr, vous pouvez poser des questions à tout moment." })!);
    }

    // ─── TEST 5: Tool error handling ──────────────────────────────────────

    [Fact]
    public async Task Tool_execution_error_is_reported_to_LLM_and_handled()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new FailingAITool());

        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "fail_tool", new Dictionary<string, string>())
                });
            }
            // LLM receives error and generates graceful response
            var hasError = request.Messages.Any(m =>
                m.Role == AIMessageRole.Tool && m.Content.Contains("Simulated tool failure"));
            return AIResponse.Text(hasError
                ? "The tool failed due to a simulated error. Let me explain what happened."
                : "I completed the operation.");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        var result = await aiService.ChatAsync("Do something that will fail");

        Assert.True(result.Success);
        Assert.Contains("error", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, callCount);
    }

    // ─── Tool definitions include all registered tools ─────────────────────

    [Fact]
    public async Task Tool_definitions_include_all_tools_when_sent_to_LLM()
    {
        var registry = CreateFullRegistry();
        AIRequest? capturedRequest = null;

        var provider = new MockAIProvider(request =>
        {
            capturedRequest = request;
            return AIResponse.Text("ok");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var aiService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);

        await aiService.ChatAsync("test");

        Assert.NotNull(capturedRequest);
        var toolNames = capturedRequest!.Tools.Select(t => t.Name).ToList();
        Assert.Contains("file_system", toolNames);
        Assert.Contains("terminal", toolNames);
        Assert.Contains("process", toolNames);
        Assert.Contains("browser", toolNames);
        Assert.Contains("clipboard", toolNames);
        Assert.Contains("windows", toolNames);
        Assert.Contains("date_time", toolNames);
        Assert.Contains("system_info", toolNames);
    }

    // ─── AIServiceAdapter.StreamChatAsync tool loop ────────────────────────

    [Fact]
    public async Task AIServiceAdapter_streams_tool_markers_and_final_text()
    {
        var registry = CreateFullRegistry();
        var callCount = 0;
        var provider = new MockAIProvider(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                return AIResponse.WithToolCalls(new[]
                {
                    new AIToolCall("call-1", "date_time", new Dictionary<string, string>())
                });
            }
            return AIResponse.Text("The current time is 10:30 AM.");
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var innerAIService = new AIService(provider, registry, executor, eventBus, CreateMemoryService(), NullLogger<AIService>.Instance);
        var adapter = new AIServiceAdapter(innerAIService, provider, new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance), registry, executor, NullLogger<AIServiceAdapter>.Instance);

        var tokens = new List<string>();
        await foreach (var token in adapter.StreamChatAsync("What time is it?"))
        {
            tokens.Add(token);
        }

        var fullOutput = string.Join("", tokens);
        Assert.Contains("The current time is 10:30 AM.", fullOutput);
    }

    // ─── Conversation preserves tool_calls in assistant messages ──────────

    [Fact]
    public void Conversation_preserves_tool_calls_in_assistant_messages()
    {
        var conversation = new AIConversation("System prompt");
        conversation.AddUserMessage("Create a file");

        var toolCalls = new List<AIToolCall>
        {
            new("call-1", "file_system", new Dictionary<string, string>
            {
                ["action"] = "create_file",
                ["path"] = @"C:\test.txt",
                ["content"] = "hello"
            })
        };

        conversation.AddAssistantWithToolCalls("", toolCalls);
        conversation.AddToolResult("call-1", "file_system", "File created");

        var messages = conversation.ToRequestMessages();
        var assistantMsg = messages[1];
        Assert.Equal(AIMessageRole.Assistant, assistantMsg.Role);
        Assert.NotNull(assistantMsg.ToolCalls);
        Assert.Single(assistantMsg.ToolCalls);
        Assert.Equal("file_system", assistantMsg.ToolCalls[0].Name);

        var toolMsg = messages[2];
        Assert.Equal(AIMessageRole.Tool, toolMsg.Role);
        Assert.Equal("call-1", toolMsg.ToolCallId);
    }

    // ─── Text-based tool call parsing ──────────────────────────────────────

    [Fact]
    public void Parse_multiple_text_tool_calls_from_single_response()
    {
        var toolDefinitions = new[]
        {
            new AIToolDefinition("file_system", "file ops", new Dictionary<string, AIToolProperty>
            {
                ["action"] = new("string", "action"),
                ["path"] = new("string", "path"),
                ["content"] = new("string", "content")
            }),
            new AIToolDefinition("terminal", "run commands", new Dictionary<string, AIToolProperty>
            {
                ["execute_command"] = new("string", "command")
            }),
            new AIToolDefinition("date_time", "time", new Dictionary<string, AIToolProperty>())
        };

        var content = @"<tool call>
file_system(action=""create_file"", path=""C:\Users\user\Desktop\test.txt"", content=""hello"")
  <tool call>
terminal(execute_command=""start chrome"")
";
        var calls = AIServiceAdapter.TryParseTextToolCalls(content, toolDefinitions);

        Assert.Equal(2, calls.Count);
        Assert.Equal("file_system", calls[0].Name);
        Assert.Equal("create_file", calls[0].Args["action"]);
        Assert.Equal(@"C:\Users\user\Desktop\test.txt", calls[0].Args["path"]);
        Assert.Equal("hello", calls[0].Args["content"]);
        Assert.Equal("terminal", calls[1].Name);
        Assert.Equal("start chrome", calls[1].Args["execute_command"]);
    }

    [Fact]
    public void Parse_single_text_tool_call_without_arguments()
    {
        var toolDefinitions = new[]
        {
            new AIToolDefinition("date_time", "time", new Dictionary<string, AIToolProperty>())
        };

        var calls = AIServiceAdapter.TryParseTextToolCalls("date_time()", toolDefinitions);

        Assert.Single(calls);
        Assert.Equal("date_time", calls[0].Name);
        Assert.Empty(calls[0].Args);
    }

    [Fact]
    public void Parse_tool_call_with_html_tool_name_tag()
    {
        var toolDefinitions = new[]
        {
            new AIToolDefinition("date_time", "time", new Dictionary<string, AIToolProperty>())
        };

        var calls = AIServiceAdapter.TryParseTextToolCalls("<tool name=\"date_time\">()</tool>", toolDefinitions);

        Assert.Single(calls);
        Assert.Equal("date_time", calls[0].Name);
        Assert.Empty(calls[0].Args);
    }

    [Fact]
    public void Parse_json_tool_call()
    {
        var toolDefinitions = new[]
        {
            new AIToolDefinition("file_system", "file ops", new Dictionary<string, AIToolProperty>
            {
                ["action"] = new("string", "action"),
                ["path"] = new("string", "path")
            }),
            new AIToolDefinition("date_time", "time", new Dictionary<string, AIToolProperty>())
        };

        var content = "<tool call>\n{\"name\": \"date_time\", \"arguments\": {}}\n</tool call>";
        var calls = AIServiceAdapter.TryParseTextToolCalls(content, toolDefinitions);

        Assert.Single(calls);
        Assert.Equal("date_time", calls[0].Name);
        Assert.Empty(calls[0].Args);
    }

    [Fact]
    public void Parse_tool_call_wrapped_in_toolname_tags()
    {
        var toolDefinitions = new[]
        {
            new AIToolDefinition("browser", "search the web", new Dictionary<string, AIToolProperty>
            {
                ["action"] = new("string", "action"),
                ["query"] = new("string", "query")
            })
        };

        var content = "<browser>(action=\"web_search\", query=\"phone comparison\")</browser>";
        var calls = AIServiceAdapter.TryParseTextToolCalls(content, toolDefinitions);

        Assert.Single(calls);
        Assert.Equal("browser", calls[0].Name);
        Assert.Equal("web_search", calls[0].Args["action"]);
        Assert.Equal("phone comparison", calls[0].Args["query"]);
    }

    [Fact]
    public void Parse_does_not_match_non_tool_text()
    {
        var toolDefinitions = new[]
        {
            new AIToolDefinition("file_system", "file ops", new Dictionary<string, AIToolProperty>
            {
                ["action"] = new("string", "action")
            })
        };

        var calls = AIServiceAdapter.TryParseTextToolCalls("The file has been created successfully.", toolDefinitions);

        Assert.Empty(calls);
    }
}
