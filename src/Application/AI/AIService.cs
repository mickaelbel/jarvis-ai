using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Events.Agents;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.AI;

public sealed class AIService
{
    private readonly IAIProvider _provider;
    private readonly IToolRegistry _toolRegistry;
    private readonly IToolExecutor _toolExecutor;
    private readonly IEventBus _eventBus;
    private readonly IMemoryService _memoryService;
    private readonly IMemorySettingsStore? _settingsStore;
    private readonly ILogger<AIService> _logger;
    private const int MaxToolRounds = 5;
    private const string MemoryCategory = "conversation";

    public AIService(
        IAIProvider provider,
        IToolRegistry toolRegistry,
        IToolExecutor toolExecutor,
        IEventBus eventBus,
        IMemoryService memoryService,
        ILogger<AIService> logger,
        IMemorySettingsStore? settingsStore = null)
    {
        _provider = provider;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _eventBus = eventBus;
        _memoryService = memoryService;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public async Task<AIResponse> ChatAsync(string userMessage, AIConversation? conversation = null, string? model = null, CancellationToken cancellationToken = default)
    {
        conversation ??= new AIConversation(BuildSystemPrompt());
        conversation.AddUserMessage(userMessage);

        _logger.LogInformation("[AIService] User message: \"{Message}\" (Provider={Provider}, Model={Model})", userMessage, _provider.Name, model ?? "default");

        await SaveToMemoryAsync(userMessage, MemoryType.Conversation, "user", cancellationToken);

        var supportsTools = ModelCapabilities.SupportsTools(model);
        var toolDefinitions = supportsTools ? BuildToolDefinitions() : Array.Empty<AIToolDefinition>();
        var rounds = 0;

        while (rounds < MaxToolRounds)
        {
            rounds++;
            var currentTools = toolDefinitions;
            var request = new AIRequest(
                systemPrompt: conversation.SystemPrompt,
                messages: conversation.ToRequestMessages(),
                tools: currentTools,
                model: model,
                temperature: 0.2f);

            _logger.LogDebug("[AIService] Round {Round} - Calling provider (Tools={ToolCount})", rounds, toolDefinitions.Count);

            AIResponse response;
            try
            {
                response = await _provider.ChatAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AIService] Provider error on round {Round}", rounds);
                return AIResponse.Failed($"AI provider error: {ex.Message}");
            }

            if (!response.Success)
            {
                _logger.LogWarning("[AIService] Provider returned error: {Error}", response.ErrorMessage);
                return response;
            }

            if (!response.HasToolCalls)
            {
                if (string.IsNullOrWhiteSpace(response.Content))
                {
                    _logger.LogWarning("[AIService] Empty LLM response (round {Rounds}); re-prompting", rounds);
                    conversation.AddMessage(AIMessage.System("Your previous response was empty. Provide a final answer describing what was done, or write a tool call to continue the task."));
                    continue;
                }

                if (supportsTools)
                {
                    var textToolCalls = JarvisAI.Application.Security.AIServiceAdapter.TryParseTextToolCalls(response.Content, toolDefinitions);
                    if (textToolCalls.Count > 0)
                    {
                        _logger.LogInformation("[AIService] Text-based tool calls detected: {Count}", textToolCalls.Count);

                        var toolCalls = textToolCalls
                            .Select(c => new AIToolCall(Guid.NewGuid().ToString("N")[..12], c.Name, c.Args))
                            .ToList();
                        conversation.AddAssistantWithToolCalls(response.Content, toolCalls);

                        foreach (var toolCall in toolCalls)
                        {
                            var toolContext = new AgentContext(
                                toolCall.Name,
                                source: "ai_service",
                                new Dictionary<string, object>
                                {
                                    ["toolCallId"] = toolCall.Id,
                                    ["arguments"] = toolCall.Arguments
                                });

                            var toolResult = await _toolExecutor.ExecuteAsync(toolCall.Name, toolContext, cancellationToken);

                            var resultContent = toolResult.Success ? toolResult.Output : $"Error: {toolResult.ErrorMessage}";
                            conversation.AddToolResult(toolCall.Id, toolCall.Name, resultContent);

                            await _eventBus.PublishAsync(
                                new AgentToolExecutedEvent(toolCall.Name, toolResult.Success, toolContext.CorrelationId, TimeSpan.Zero, toolResult.ErrorMessage),
                                cancellationToken);

                            if (toolResult.Success)
                            {
                                await SaveToMemoryAsync(
                                    $"Tool {toolCall.Name}: {resultContent}",
                                    MemoryType.ToolResult, toolCall.Name, cancellationToken,
                                    importance: 0.3f);
                            }

                            _logger.LogInformation("[AIService] Text tool {ToolName} result: {Success}", toolCall.Name, toolResult.Success);
                        }
                        continue;
                    }
                }

                _logger.LogInformation("[AIService] Final text response (rounds={Rounds})", rounds);
                conversation.AddAssistantMessage(response.Content);
                await SaveToMemoryAsync(response.Content, MemoryType.Conversation, "assistant", cancellationToken);
                return response;
            }

            _logger.LogInformation("[AIService] Tool calls requested: {Count} (round {Round})", response.ToolCalls.Count, rounds);
            conversation.AddAssistantWithToolCalls(response.Content, response.ToolCalls);

            foreach (var toolCall in response.ToolCalls)
            {
                _logger.LogInformation("[AIService] Executing tool: {ToolName} (Id={ToolCallId})", toolCall.Name, toolCall.Id);

                var toolContext = new AgentContext(
                    toolCall.Name,
                    source: "ai_service",
                    new Dictionary<string, object>
                    {
                        ["toolCallId"] = toolCall.Id,
                        ["arguments"] = toolCall.Arguments
                    });

                var toolResult = await _toolExecutor.ExecuteAsync(toolCall.Name, toolContext, cancellationToken);

                var resultContent = toolResult.Success ? toolResult.Output : $"Error: {toolResult.ErrorMessage}";
                conversation.AddToolResult(toolCall.Id, toolCall.Name, resultContent);

                await _eventBus.PublishAsync(
                    new AgentToolExecutedEvent(toolCall.Name, toolResult.Success, toolContext.CorrelationId, TimeSpan.Zero, toolResult.ErrorMessage),
                    cancellationToken);

                if (toolResult.Success)
                {
                    await SaveToMemoryAsync(
                        $"Tool {toolCall.Name}: {resultContent}",
                        MemoryType.ToolResult, toolCall.Name, cancellationToken,
                        importance: 0.3f);
                }

                _logger.LogInformation("[AIService] Tool {ToolName} result: {Success}", toolCall.Name, toolResult.Success);
            }
        }

        _logger.LogWarning("[AIService] Max tool rounds ({MaxRounds}) reached", MaxToolRounds);
        return AIResponse.Failed("Maximum tool execution rounds reached without final response.");
    }

    private async Task SaveToMemoryAsync(string content, MemoryType type, string subcategory, CancellationToken cancellationToken, float importance = 0.5f)
    {
        if (_settingsStore?.Get() is { MemoryEnabled: false }) return;
        try
        {
            var key = $"{subcategory}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
            await _memoryService.SaveAsync(key, content, type, MemoryCategory, importance, ttl: TimeSpan.FromHours(24), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AIService] Failed to save to memory");
        }
    }

    private string BuildSystemPrompt()
    {
        return AgentSystemPrompt.Build(BuildToolDefinitions());
    }

    private IReadOnlyList<AIToolDefinition> BuildToolDefinitions()
        => ToolDefinitionBuilder.Build(_toolRegistry);
}
