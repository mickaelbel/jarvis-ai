using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;

namespace JarvisAI.Application.Plugins;

public sealed class PluginContext
{
    private readonly List<ITool> _tools = new();
    private readonly List<(Application.Commands.ICommand Command, Application.Commands.ICommandHandler Handler)> _commands = new();
    private readonly List<IDisposable> _eventSubscriptions = new();
    private readonly PluginMetadata? _metadata;
    private readonly PluginPermissionManager? _permissionManager;

    public IEventBus EventBus { get; }
    public IToolRegistry ToolRegistry { get; }
    public IServiceProvider ServiceProvider { get; }
    public string PluginDirectory { get; }
    public IReadOnlyList<ITool> Tools => _tools.AsReadOnly();

    public PluginContext(
        IEventBus eventBus,
        IToolRegistry toolRegistry,
        IServiceProvider serviceProvider,
        string pluginDirectory,
        PluginMetadata? metadata = null,
        PluginPermissionManager? permissionManager = null)
    {
        EventBus = eventBus;
        ToolRegistry = toolRegistry;
        ServiceProvider = serviceProvider;
        PluginDirectory = pluginDirectory;
        _metadata = metadata;
        _permissionManager = permissionManager;
    }

    public void RegisterTool(ITool tool)
    {
        if (_permissionManager is not null && _metadata is not null)
        {
            if (!_permissionManager.CanRegisterTool(_metadata, tool))
            {
                throw new System.Security.SecurityException(
                    $"Plugin '{_metadata.Name}' is not allowed to register tool '{tool.Name}' " +
                    $"(risk {tool.RiskLevel} exceeds granted permission {_metadata.PermissionLevel}).");
            }
        }

        _tools.Add(tool);
        ToolRegistry.Register(tool);
    }

    public void RegisterCommand(Application.Commands.ICommand command, Application.Commands.ICommandHandler handler)
    {
        _commands.Add((command, handler));
    }

    public void TrackSubscription(IDisposable subscription)
    {
        _eventSubscriptions.Add(subscription);
    }

    public void UnregisterTools()
    {
        foreach (var tool in _tools)
        {
            ToolRegistry.Unregister(tool.Name);
        }
        _tools.Clear();
    }

    public void DisposeSubscriptions()
    {
        foreach (var sub in _eventSubscriptions)
        {
            sub.Dispose();
        }
        _eventSubscriptions.Clear();
    }
}
