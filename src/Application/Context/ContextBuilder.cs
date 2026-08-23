using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.AutoImprovement;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Runtime.InteropServices;

namespace JarvisAI.Application.Context;

public sealed class ContextSection
{
    public string Title { get; }
    public string Content { get; }
    public int MaxLength { get; }

    public ContextSection(string title, string content, int maxLength = 4000)
    {
        Title = title;
        MaxLength = Math.Max(256, maxLength);
        Content = Truncate(content, MaxLength);
    }

    public string Render() => $"=== {Title} ===\n{Content}";

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(none)";
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
}

public sealed class ContextBundle
{
    public string Goal { get; }
    public string? CommandText { get; }
    public string? Source { get; }
    public Guid CorrelationId { get; }
    public ModelSelectionMode Mode { get; }
    public IReadOnlyList<MemoryEntry> RelevantMemories { get; }
    public IReadOnlyList<string> ActivePlugins { get; }
    public IReadOnlyList<ITool> AvailableTools { get; }
    public string? OllamaStatus { get; }
    public IReadOnlyList<ContextSection> Sections { get; }

    public ContextBundle(
        string goal,
        Guid correlationId,
        ModelSelectionMode mode,
        IReadOnlyList<MemoryEntry> relevantMemories,
        IReadOnlyList<string> activePlugins,
        IReadOnlyList<ITool> availableTools,
        string? ollamaStatus,
        IReadOnlyList<ContextSection> sections,
        string? commandText = null,
        string? source = null)
    {
        Goal = goal;
        CorrelationId = correlationId;
        Mode = mode;
        RelevantMemories = relevantMemories;
        ActivePlugins = activePlugins;
        AvailableTools = availableTools;
        OllamaStatus = ollamaStatus;
        Sections = sections;
        CommandText = commandText;
        Source = source;
    }

    public IReadOnlyList<string> ToolNames => AvailableTools.Select(t => t.Name).ToList();

    public string Render(int maxTotalLength = 12000)
    {
        var sb = new StringBuilder();
        foreach (var section in Sections)
        {
            sb.AppendLine(section.Render());
            sb.AppendLine();
        }

        var rendered = sb.ToString().TrimEnd();
        if (rendered.Length <= maxTotalLength) return rendered;
        return rendered[..(maxTotalLength - 3)] + "...";
    }
}

public interface IContextBuilder
{
    Task<ContextBundle> BuildAsync(AgentRequest request, CancellationToken cancellationToken = default);
}

public sealed class ContextBuilder : IContextBuilder
{
    private readonly IMemoryService _memory;
    private readonly IPluginManager _pluginManager;
    private readonly IToolRegistry _toolRegistry;
    private readonly ISelfImprovementManager _selfImprovement;
    private readonly IAIProvider _provider;
    private readonly IModelRouter _router;
    private readonly ILogger<ContextBuilder> _logger;

    private const int SystemSectionLength = 1000;
    private const int OllamaSectionLength = 500;
    private const int PluginsSectionLength = 2000;
    private const int ToolsSectionLength = 4000;
    private const int MemorySectionLength = 4000;
    private const int ConversationSectionLength = 3000;
    private const int LessonsSectionLength = 1500;

    public ContextBuilder(
        IMemoryService memory,
        IPluginManager pluginManager,
        IToolRegistry toolRegistry,
        ISelfImprovementManager selfImprovement,
        IAIProvider provider,
        IModelRouter router,
        ILogger<ContextBuilder> logger)
    {
        _memory = memory;
        _pluginManager = pluginManager;
        _toolRegistry = toolRegistry;
        _selfImprovement = selfImprovement;
        _provider = provider;
        _router = router;
        _logger = logger;
    }

    public async Task<ContextBundle> BuildAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        var goal = request.Goal?.Trim() ?? string.Empty;
        var correlationId = request.CorrelationId ?? Guid.NewGuid();

        var systemSection = BuildSystemSection();
        var ollamaSection = BuildOllamaSection();
        var plugins = GetActivePlugins();
        var pluginsSection = BuildPluginsSection(plugins);
        var tools = _toolRegistry.GetAll();
        var toolsSection = BuildToolsSection(tools);
        var memories = await BuildMemorySectionAsync(goal, cancellationToken);
        var conversationSection = BuildConversationSection(request);
        var lessons = BuildLessonsSection();

        var sections = new List<ContextSection>
        {
            new("SYSTEM", systemSection, SystemSectionLength),
            new("OLLAMA", ollamaSection, OllamaSectionLength),
            new("ACTIVE PLUGINS", pluginsSection, PluginsSectionLength),
            new("AVAILABLE TOOLS", toolsSection, ToolsSectionLength),
            new("MEMORY", memories, MemorySectionLength),
            new("SELF-IMPROVEMENT", lessons, LessonsSectionLength),
            new("CONVERSATION", conversationSection, ConversationSectionLength)
        };

        _logger.LogInformation("[ContextBuilder] Built context for goal: {Goal} (Plugins={PluginCount}, Tools={ToolCount}, Memory chars={MemoryLength})",
            goal, plugins.Count, tools.Count, memories.Length);

        return new ContextBundle(
            goal: goal,
            correlationId: correlationId,
            mode: request.Mode,
            relevantMemories: Array.Empty<MemoryEntry>(),
            activePlugins: plugins,
            availableTools: tools,
            ollamaStatus: ollamaSection,
            sections: sections,
            commandText: request.CommandText,
            source: request.Source);
    }

    private static string BuildSystemSection()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Platform: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine($"User: {Environment.UserName}");
        sb.AppendLine($"Date/time (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}");
        sb.AppendLine($"Date/time (local): {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        return sb.ToString();
    }

    private string BuildOllamaSection()
    {
        var available = false;
        try { available = _provider.IsAvailable; }
        catch { available = false; }

        var sb = new StringBuilder();
        sb.AppendLine(available ? "Ollama is available." : "Ollama is NOT available - model calls may fail.");
        sb.AppendLine($"Fast model: {_router.Options.FastModel}");
        sb.AppendLine($"Powerful model: {_router.Options.ReasoningModel}");
        if (_router.LastRoute is not null)
            sb.AppendLine($"Last resolved model: {_router.LastRoute.Model} ({_router.LastRoute.Reason})");
        return sb.ToString();
    }

    private IReadOnlyList<string> GetActivePlugins()
    {
        try
        {
            return _pluginManager.GetAllMetadata()
                .Where(m => m.State == PluginState.Running)
                .Select(m => $"{m.Name} ({m.Id})")
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ContextBuilder] Failed to enumerate plugins");
            return Array.Empty<string>();
        }
    }

    private static string BuildPluginsSection(IReadOnlyList<string> plugins)
    {
        if (plugins.Count == 0) return "No plugins are currently active.";
        var sb = new StringBuilder();
        foreach (var plugin in plugins)
            sb.AppendLine($"- {plugin}");
        return sb.ToString();
    }

    private static string BuildToolsSection(IReadOnlyList<ITool> tools)
    {
        if (tools.Count == 0) return "No tools registered.";
        var sb = new StringBuilder();
        foreach (var tool in tools)
            sb.AppendLine($"- {tool.Name} [{tool.Category}] (risk={tool.RiskLevel}): {tool.Description}");
        return sb.ToString();
    }

    private async Task<string> BuildMemorySectionAsync(string goal, CancellationToken cancellationToken)
    {
        try
        {
            var context = await _memory.BuildContextAsync(goal, project: null, limitPerScope: 5, cancellationToken);
            if (context.TotalCount > 0)
                return context.Render();
            return "No relevant memories found.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ContextBuilder] Failed to build memory context");
            return "Memory unavailable.";
        }
    }

    private string BuildLessonsSection()
    {
        try
        {
            var lessons = _selfImprovement.GetLessons(15);
            if (lessons.Count == 0)
                return "No lessons recorded yet. Use add_lesson to remember durable facts; use create_tool when a capability is missing.";

            var sb = new StringBuilder();
            foreach (var lesson in lessons)
                sb.AppendLine("- " + lesson);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ContextBuilder] Failed to build self-improvement section");
            return "Lessons unavailable.";
        }
    }

    private static string BuildConversationSection(AgentRequest request)
    {
        if (request.Metadata is not null &&
            request.Metadata.TryGetValue("recent_conversation", out var recent) &&
            !string.IsNullOrWhiteSpace(recent))
        {
            return recent;
        }

        return "No recent conversation provided.";
    }
}
