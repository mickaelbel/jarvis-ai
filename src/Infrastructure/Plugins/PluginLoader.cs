using JarvisAI.Application.Plugins;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.Loader;

namespace JarvisAI.Infrastructure.Plugins;

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }
}

public sealed class PluginLoader
{
    private readonly ILogger<PluginLoader> _logger;
    private readonly Dictionary<string, PluginLoadContext> _contexts = new();
    private readonly Dictionary<string, WeakReference<Assembly>> _assemblies = new();

    public PluginLoader(ILogger<PluginLoader> logger)
    {
        _logger = logger;
    }

    public Assembly LoadPlugin(string pluginId, string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Plugin assembly not found: {fullPath}");

        _logger.LogInformation("[PluginLoader] Loading assembly: {Path}", fullPath);

        var context = new PluginLoadContext(fullPath);
        var assembly = context.LoadFromAssemblyPath(fullPath);

        _contexts[pluginId] = context;
        _assemblies[pluginId] = new WeakReference<Assembly>(assembly);

        _logger.LogInformation("[PluginLoader] Loaded: {AssemblyName}", assembly.FullName);
        return assembly;
    }

    public void UnloadPlugin(string pluginId)
    {
        if (_contexts.TryGetValue(pluginId, out var context))
        {
            _logger.LogInformation("[PluginLoader] Unloading plugin: {PluginId}", pluginId);
            context.Unload();
            _contexts.Remove(pluginId);
            _assemblies.Remove(pluginId);
        }
    }

    public IReadOnlyList<PluginMetadata> DiscoverPlugins(string pluginsDirectory)
    {
        var metadata = new List<PluginMetadata>();

        if (!Directory.Exists(pluginsDirectory))
        {
            _logger.LogWarning("[PluginLoader] Plugins directory not found: {Path}", pluginsDirectory);
            return metadata;
        }

        var dllFiles = Directory.GetFiles(pluginsDirectory, "*.dll", SearchOption.AllDirectories);

        foreach (var dllPath in dllFiles)
        {
            try
            {
                var meta = ExtractMetadata(dllPath);
                if (meta is not null)
                {
                    metadata.Add(meta);
                    _logger.LogInformation("[PluginLoader] Discovered plugin: {Name} v{Version} at {Path}",
                        meta.Name, meta.Version, dllPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PluginLoader] Failed to inspect assembly: {Path}", dllPath);
            }
        }

        return metadata;
    }

    private PluginMetadata? ExtractMetadata(string assemblyPath)
    {
        var assembly = Assembly.LoadFrom(assemblyPath);
        var pluginType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        if (pluginType is null)
            return null;

        var tempInstance = (IPlugin)Activator.CreateInstance(pluginType)!;
        var metadata = new PluginMetadata
        {
            Id = string.IsNullOrWhiteSpace(tempInstance.Metadata.Id) ? pluginType.FullName ?? pluginType.Name : tempInstance.Metadata.Id,
            Name = tempInstance.Metadata.Name,
            Description = tempInstance.Metadata.Description,
            Author = tempInstance.Metadata.Author,
            Version = tempInstance.Metadata.Version,
            AssemblyPath = assemblyPath,
            TypeName = pluginType.FullName ?? pluginType.Name,
            Dependencies = new List<string>(tempInstance.Metadata.Dependencies),
            Permissions = new List<string>(tempInstance.Metadata.Permissions),
            PermissionLevel = tempInstance.Metadata.PermissionLevel
        };

        if (tempInstance is IDisposable disposable)
            disposable.Dispose();

        return metadata;
    }
}
