using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace JarvisAI.Infrastructure.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<ITool>> _toolFactories = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Enregistre un tool avec une factory pour le lazy loading.
    /// Le tool ne sera créé que lors de la première demande par son nom.
    /// </summary>
    public void RegisterFactory(string name, Func<ITool> factory)
    {
        _toolFactories.TryAdd(name, factory);
        Interlocked.Increment(ref _version);
        _logger.LogInformation("[ToolRegistry] Registered lazy tool factory: {ToolName}", name);
    }

    private void EnsureResolved()
    {
        if (_resolved || _toolResolver is null) return;
        _resolved = true;
        foreach (var tool in _toolResolver())
            Register(tool);
    }

    private ITool? ResolveByName(string name)
    {
        // D'abord chercher dans les tools déjà instanciés
        if (_tools.TryGetValue(name, out var existing))
            return existing;

        // Sinon chercher dans les factories et créer à la volée
        if (_toolFactories.TryGetValue(name, out var factory))
        {
            try
            {
                var tool = factory();
                _tools.TryAdd(name, tool);
                _logger.LogInformation("[ToolRegistry] Lazy-created tool: {ToolName}", name);
                return tool;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ToolRegistry] Failed to create tool: {ToolName}", name);
                return null;
            }
        }

        return null;
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
        _toolFactories.TryRemove(name, out _);
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
        return ResolveByName(name);
    }

    public IReadOnlyList<ITool> GetByCategory(string category)
    {
        EnsureResolved();
        // Forcer la résolution de toutes les factories pour la recherche par catégorie
        foreach (var kv in _toolFactories)
            ResolveByName(kv.Key);
        return _tools.Values
            .Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<ITool> GetAll()
    {
        EnsureResolved();
        // Forcer la résolution de toutes les factories
        foreach (var kv in _toolFactories)
            ResolveByName(kv.Key);
        return _tools.Values.ToList().AsReadOnly();
    }
}
