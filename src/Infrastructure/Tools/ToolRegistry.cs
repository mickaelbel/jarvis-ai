using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ToolRegistry> _logger;
    private int _version;
    private Func<IEnumerable<ITool>>? _toolResolver;
    private bool _resolved;

    public int Version => _version;

    public ToolRegistry(ILogger<ToolRegistry> logger)
    {
        _logger = logger;
    }

    public void SetToolResolver(Func<IEnumerable<ITool>> resolver)
    {
        _toolResolver = resolver;
    }

    private void EnsureResolved()
    {
        if (_resolved || _toolResolver is null) return;
        _resolved = true;
        foreach (var tool in _toolResolver())
            Register(tool);
    }

    public void Register(ITool tool)
    {
        if (_tools.TryAdd(tool.Name, tool))
        {
            Interlocked.Increment(ref _version);
            _logger.LogInformation("[ToolRegistry] Registered tool: {ToolName} (Category: {Category})",
                tool.Name, tool.Category);
        }
        else
        {
            _logger.LogWarning("[ToolRegistry] Tool already registered: {ToolName}", tool.Name);
        }
    }

    public bool Unregister(string name)
    {
        var removed = _tools.TryRemove(name, out _);
        if (removed)
        {
            Interlocked.Increment(ref _version);
            _logger.LogInformation("[ToolRegistry] Unregistered tool: {ToolName}", name);
        }
        return removed;
    }

    public ITool? GetByName(string name)
    {
        EnsureResolved();
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public IReadOnlyList<ITool> GetByCategory(string category)
    {
        EnsureResolved();
        return _tools.Values
            .Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<ITool> GetAll()
    {
        EnsureResolved();
        return _tools.Values.ToList().AsReadOnly();
    }
}
