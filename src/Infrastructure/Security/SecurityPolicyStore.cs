using System.Collections.Concurrent;
using JarvisAI.Application.Security;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Security;

public sealed class SecurityPolicyStore
{
    private readonly ConcurrentDictionary<string, SecurityAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SecurityPolicyStore> _logger;

    public SecurityPolicyStore(ILogger<SecurityPolicyStore> logger)
    {
        _logger = logger;
        InitializeDefaults();
    }

    private void InitializeDefaults()
    {
        RegisterAction(SecurityAction.LowRisk("date_time", "Read current date and time"));
        RegisterAction(SecurityAction.LowRisk("system_info", "Read system information"));
        RegisterAction(SecurityAction.LowRisk("memory", "Access memory store"));
        RegisterAction(SecurityAction.MediumRisk("file_write", "Write to files"));
        RegisterAction(SecurityAction.MediumRisk("settings_change", "Change system settings"));
        RegisterAction(SecurityAction.HighRisk("file_delete", "Delete files"));
        RegisterAction(SecurityAction.HighRisk("process_kill", "Kill running processes"));
        RegisterAction(SecurityAction.HighRisk("system_command", "Execute system commands"));
        RegisterAction(SecurityAction.HighRisk("network_access", "Access network resources"));
        RegisterAction(SecurityAction.HighRisk("hardware_control", "Control hardware devices"));
    }

    public void RegisterAction(SecurityAction action)
    {
        _actions[action.Name] = action;
        _logger.LogDebug("[SecurityPolicyStore] Registered action: {Name} (Risk={Risk})", action.Name, action.RiskLevel);
    }

    public SecurityAction? GetAction(string name)
    {
        _actions.TryGetValue(name, out var action);
        return action;
    }

    public IReadOnlyList<SecurityAction> GetAllActions()
    {
        return _actions.Values.ToList().AsReadOnly();
    }

    public bool RemoveAction(string name)
    {
        return _actions.TryRemove(name, out _);
    }
}
