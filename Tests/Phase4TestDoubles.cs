using JarvisAI.Application.Agents;
using JarvisAI.Application.AI;
using JarvisAI.Application.AutoImprovement;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;

namespace JarvisAI.Tests;

internal sealed class TestTool : ITool
{
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ToolResult>> _handler;

    public TestTool(
        string name,
        string description = "test tool",
        string category = "test",
        SecurityRiskLevel riskLevel = SecurityRiskLevel.Low,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ToolResult>>? handler = null)
    {
        Name = name;
        Description = description;
        Category = category;
        RiskLevel = riskLevel;
        _handler = handler ?? ((_, _) => Task.FromResult(ToolResult.Succeeded("ok")));
    }

    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public SecurityRiskLevel RiskLevel { get; }
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        => _handler(parameters, cancellationToken);
}

internal sealed class FakeToolExecutor : IToolExecutor
{
    private readonly Func<string, ToolResult>? _handler;
    public List<string> Calls { get; } = new();
    public List<AgentContext> Contexts { get; } = new();

    public FakeToolExecutor(Func<string, ToolResult>? handler = null)
        => _handler = handler;

    public async Task<ToolResult> ExecuteAsync(string toolName, AgentContext context, CancellationToken cancellationToken = default)
    {
        await Task.Delay(60, cancellationToken);
        lock (Calls)
        {
            Calls.Add(toolName);
            Contexts.Add(context);
        }
        if (_handler is not null) return _handler(toolName);
        return ToolResult.Succeeded($"result of {toolName}");
    }
}

internal sealed class FakeSelfImprovementManager : ISelfImprovementManager
{
    public bool SafeMode { get; set; }
    public List<AutoToolSpec> Tools { get; } = new();
    public List<string> Lessons { get; } = new();

    public void LoadPersisted() { }
    public AutoToolCreateResult CreateTool(AutoToolSpec spec, string source)
    {
        Tools.Add(spec);
        return AutoToolCreateResult.Ok(spec.Name);
    }
    public bool EnableTool(string name) => true;
    public bool DisableTool(string name) => true;
    public bool RemoveTool(string name) => Tools.RemoveAll(t => t.Name == name) > 0;
    public IReadOnlyList<AutoToolSpec> ListTools() => Tools;
    public AutoToolSpec? GetTool(string name) => Tools.FirstOrDefault(t => t.Name == name);
    public void AddLesson(string lesson) => Lessons.Add(lesson);
    public IReadOnlyList<string> GetLessons(int maxCount = 25) => Lessons.TakeLast(maxCount).ToList();
}

internal sealed class FakePluginManager : IPluginManager
{
    private readonly IReadOnlyList<PluginMetadata> _metadata;
    private readonly bool _throwOnEnumerate;

    public FakePluginManager(bool throwOnEnumerate = false, params PluginMetadata[] metadata)
    {
        _throwOnEnumerate = throwOnEnumerate;
        _metadata = metadata;
    }

    public IReadOnlyList<PluginMetadata> GetAllMetadata()
    {
        if (_throwOnEnumerate) throw new InvalidOperationException("plugin enumeration failed");
        return _metadata;
    }

    public PluginMetadata? GetMetadata(string pluginId) => _metadata.FirstOrDefault(m => m.Id == pluginId);
    public Task DiscoverAsync(string pluginsDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task LoadAsync(string pluginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UnloadAsync(string pluginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartAsync(string pluginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(string pluginId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public IPlugin? GetPlugin(string pluginId) => null;
    public IReadOnlyList<string> GetPluginTools(string pluginId) => Array.Empty<string>();
}

internal sealed class FakeMemoryService : IMemoryService
{
    private readonly Func<string, Task<MemoryContext>>? _buildContext;
    private readonly Func<string, Task<MemoryEntry?>>? _get;

    public FakeMemoryService(
        Func<string, Task<MemoryContext>>? buildContext = null,
        Func<string, Task<MemoryEntry?>>? get = null)
    {
        _buildContext = buildContext;
        _get = get;
    }

    public Task<MemoryEntry> SaveAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new MemoryEntry { Key = key, Content = content, Type = type, Category = category, Importance = importance });

    public Task<MemoryEntry> SaveMemoryAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, MemoryTier tier = MemoryTier.LongTerm, string? project = null, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new MemoryEntry { Key = key, Content = content, Type = type, Category = category, Importance = importance, Tier = tier });

    public Task<MemoryEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        => _get is not null ? _get(key) : Task.FromResult<MemoryEntry?>(null);

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>());

    public Task<IReadOnlyList<MemoryEntry>> SearchSemanticAsync(string query, int limit = 10, MemoryTier? tier = null, string? category = null, string? project = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>());

    public Task<MemoryContext> BuildContextAsync(string query, string? project = null, int limitPerScope = 6, CancellationToken cancellationToken = default)
        => _buildContext is not null ? _buildContext(query) : Task.FromResult(new MemoryContext());

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    public Task<IReadOnlyList<MemoryEntry>> GetContextAsync(string category, int maxEntries = 20, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>());
}

internal sealed class HangingProvider : IAIProvider
{
    public string Name => "Hanging";
    public bool IsAvailable => true;
    public IReadOnlyList<string> KnownModels => Array.Empty<string>();
    public bool MatchesModel(string? model) => false;

    public async Task<AIResponse> ChatAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        return AIResponse.Text("never returns");
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamChatAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
