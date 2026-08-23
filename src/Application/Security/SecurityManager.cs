using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Security;

public sealed class SecurityManager : ISecurityManager
{
    private readonly Lazy<IToolRegistry> _toolRegistry;
    private readonly IUserConfirmationService _confirmationService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<SecurityManager> _logger;
    private SecurityOptions _options;
    private readonly IPermissionStore? _permissionStore;

    private readonly Dictionary<string, int> _actionCounts = new();
    private DateTime _windowStart = DateTime.UtcNow;

    public SecurityManager(
        Lazy<IToolRegistry> toolRegistry,
        IUserConfirmationService confirmationService,
        IEventBus eventBus,
        ILogger<SecurityManager> logger,
        SecurityOptions? options = null,
        IPermissionStore? permissionStore = null)
    {
        _toolRegistry = toolRegistry;
        _confirmationService = confirmationService;
        _eventBus = eventBus;
        _logger = logger;
        _options = options ?? new SecurityOptions();
        _permissionStore = permissionStore;
    }

    public void Configure(SecurityOptions options) => _options = options;
    public SecurityOptions GetOptions() => _options;

    public SecurityRiskLevel GetToolRiskLevel(string toolName)
    {
        var tool = _toolRegistry.Value.GetByName(toolName);
        return tool?.RiskLevel ?? SecurityRiskLevel.Low;
    }

    public bool IsPathAllowed(string path)
    {
        if (_options.Mode == OperationMode.Autonomous)
            return true;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (!IsWithinAllowedRoots(fullPath) && !IsWithinUserFolders(fullPath))
            return false;

        // Resolve reparse points (symlinks/junctions) anywhere in the path and
        // re-check the physical target: a link pointing outside the sandbox is
        // blocked even if its textual location looks allowed.
        var resolved = ResolveReparsePoints(fullPath);
        if (!string.Equals(resolved, fullPath, StringComparison.OrdinalIgnoreCase)
            && !IsWithinAllowedRoots(resolved)
            && !IsWithinUserFolders(resolved))
        {
            _logger.LogWarning("[SecurityManager] Path blocked: {Path} resolves outside allowed directories via link to {Resolved}", fullPath, resolved);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The user's personal folders (Desktop, Documents, Downloads, Pictures,
    /// Music, Videos, Favorites) are always reachable so the assistant can work
    /// with the files the user actually lives in, regardless of the configured
    /// sandbox. System folders and other users' profiles remain restricted.
    /// </summary>
    private bool IsWithinUserFolders(string fullPath)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.Favorites)
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            try
            {
                if (IsWithinPath(fullPath, Path.GetFullPath(root)))
                    return true;
            }
            catch
            {
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var downloads = Path.Combine(userProfile, "Downloads");
            if (IsWithinPath(fullPath, downloads))
                return true;
        }

        return false;
    }

    private bool IsWithinAllowedRoots(string fullPath)
    {
        var allowed = _options.AllowedPaths;
        if (allowed is null || allowed.Count == 0)
            return true;

        foreach (var allowedPath in allowed)
        {
            if (string.IsNullOrWhiteSpace(allowedPath))
                continue;

            string resolved;
            try
            {
                resolved = Path.GetFullPath(allowedPath);
            }
            catch
            {
                continue;
            }

            if (IsWithinPath(fullPath, resolved))
                return true;
        }

        _logger.LogWarning("[SecurityManager] Path blocked: {Path}", fullPath);
        return false;
    }

    /// <summary>
    /// Walks every existing component of <paramref name="fullPath"/> and, when it
    /// is a reparse point (symlink or junction), substitutes the physical target.
    /// Returns the canonical path after resolution, or the input when nothing can
    /// be resolved.
    /// </summary>
    private static string ResolveReparsePoints(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return fullPath;

            var relative = fullPath[root.Length..];
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var changed = false;

            foreach (var segment in segments)
            {
                var candidate = Path.Combine(current, segment);
                string? target = null;
                try
                {
                    if (Directory.Exists(candidate))
                    {
                        var dirTarget = Directory.ResolveLinkTarget(candidate, returnFinalTarget: true);
                        if (dirTarget is not null)
                            target = dirTarget.FullName;
                    }
                    else if (File.Exists(candidate))
                    {
                        var fileTarget = File.ResolveLinkTarget(candidate, returnFinalTarget: true);
                        if (fileTarget is not null)
                            target = fileTarget.FullName;
                    }
                }
                catch
                {
                    target = null;
                }

                if (target is not null)
                {
                    current = target;
                    changed = true;
                }
                else
                {
                    current = candidate;
                }
            }

            return changed ? Path.GetFullPath(current) : fullPath;
        }
        catch
        {
            return fullPath;
        }
    }

    private static bool IsWithinPath(string path, string allowedRoot)
    {
        if (path.Equals(allowedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = allowedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length <= prefix.Length)
            return false;

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (path[prefix.Length] == Path.DirectorySeparatorChar
                || path[prefix.Length] == Path.AltDirectorySeparatorChar);
    }

    public bool IsCommandBlacklisted(string command)
    {
        if (_options.Mode == OperationMode.Autonomous)
            return false;

        if (string.IsNullOrWhiteSpace(command)) return false;

        var isBlacklisted = _options.BlacklistedCommands.Any(blacklisted =>
            command.Contains(blacklisted, StringComparison.OrdinalIgnoreCase));

        if (isBlacklisted)
            _logger.LogWarning("[SecurityManager] Blacklisted command detected: {Command}", command);

        return isBlacklisted;
    }

    public bool IsToolWhitelisted(string toolName)
    {
        if (_options.WhitelistedTools.Count == 0) return true;
        return _options.WhitelistedTools.Contains(toolName);
    }

    public async Task<bool> IsToolAllowedAsync(string toolName, Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (!IsToolWhitelisted(toolName))
        {
            _logger.LogWarning("[SecurityManager] Tool not in whitelist: {ToolName}", toolName);
            await PublishSecurityEventAsync(toolName, SecurityRiskLevel.High, false, "denied_whitelist", correlationId, cancellationToken);
            return false;
        }

        if (CheckRateLimit())
        {
            _logger.LogWarning("[SecurityManager] Rate limit exceeded");
            await PublishSecurityEventAsync(toolName, SecurityRiskLevel.High, false, "denied_rate_limit", correlationId, cancellationToken);
            return false;
        }

        return true;
    }

    public async Task<ConfirmationResult> CheckAndConfirmAsync(string toolName, AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        if (!await IsToolAllowedAsync(toolName, context.CorrelationId, cancellationToken))
        {
            return ConfirmationResult.Denied(ConfirmationMethod.Automatic, TimeSpan.Zero, "Tool blocked by security policy");
        }

        var action = parameters.TryGetValue("action", out var a) ? a : null;
        var safetyLevel = ToolSafetyPolicy.GetLevel(toolName, GetToolRiskLevel(toolName), action);

        // N3 (critique) : confirmation TOUJOURS exigée, même en mode autonome,
        // même en mode développeur. Jamais mémorisable.
        if (_options.N3RequiresConfirmation && safetyLevel == ToolSafetyLevel.N3)
        {
            _logger.LogWarning("[SecurityManager] Action CRITIQUE (N3) : confirmation obligatoire : {ToolName}.{Action}", toolName, action);
            var n3Description = BuildN3Description(toolName, action);
            return await ConfirmAndPublishAsync(toolName, SecurityRiskLevel.High, n3Description, context, parameters, cancellationToken);
        }

        if (_options.Mode == OperationMode.Autonomous)
        {
            var autoRiskLevel = GetToolRiskLevel(toolName);
            _logger.LogWarning("[SecurityManager] AUTONOMOUS MODE: auto-confirming {ToolName} without user confirmation", toolName);
            var result = ConfirmationResult.AutoConfirmed();
            await PublishSecurityEventAsync(toolName, autoRiskLevel, true, "auto_autonomous_mode", context.CorrelationId, cancellationToken);
            return result;
        }

        var riskLevel = GetToolRiskLevel(toolName);
        var actionRisk = ComputerUseRiskClassifier.GetActionRiskLevel(toolName, action);
        var isDestructiveAction = ComputerUseRiskClassifier.IsDestructiveAction(toolName, action);

        if (isDestructiveAction)
        {
            _logger.LogWarning("[SecurityManager] Destructive action requires mandatory confirmation: {ToolName}.{Action} (ActionRisk={ActionRisk})",
                toolName, action, actionRisk);
            var description = $"Execute {toolName}.{action} (Risk: {actionRisk} - destructive action)";
            return await ConfirmAndPublishAsync(toolName, actionRisk, description, context, parameters, cancellationToken,
                $"Action: {action} (destructive)");
        }

        if (_options.AllowDisableConfirmation)
        {
            _logger.LogDebug("[SecurityManager] Developer mode: bypassing confirmation for {ToolName}", toolName);
            var result = ConfirmationResult.AutoConfirmed();
            await PublishSecurityEventAsync(toolName, riskLevel, true, "auto_developer_mode", context.CorrelationId, cancellationToken, "Bypassed: developer mode");
            return result;
        }

        // N2 mémorisé (« toujours autoriser ») : on saute la confirmation.
        if (_permissionStore is not null && safetyLevel == ToolSafetyLevel.N2
            && await _permissionStore.IsAlwaysAllowedAsync(toolName, action, cancellationToken))
        {
            _logger.LogInformation("[SecurityManager] {ToolName} toujours autorisé (N2 mémorisé), auto-confirmé", toolName);
            var result = ConfirmationResult.AutoConfirmed();
            await PublishSecurityEventAsync(toolName, riskLevel, true, "auto_always_allowed", context.CorrelationId, cancellationToken);
            return result;
        }

        if (riskLevel == SecurityRiskLevel.Low)
        {
            _logger.LogDebug("[SecurityManager] Low risk tool, auto-confirming: {ToolName}", toolName);
            var result = ConfirmationResult.AutoConfirmed();
            await PublishSecurityEventAsync(toolName, riskLevel, true, "auto_low_risk", context.CorrelationId, cancellationToken);
            return result;
        }

        bool needsConfirmation = riskLevel == SecurityRiskLevel.High
            ? _options.RequireConfirmationForHighRisk
            : _options.RequireConfirmationForMediumRisk;

        if (!needsConfirmation)
        {
            _logger.LogDebug("[SecurityManager] Confirmation not required for {RiskLevel} tool: {ToolName}", riskLevel, toolName);
            var result = ConfirmationResult.AutoConfirmed();
            await PublishSecurityEventAsync(toolName, riskLevel, true, "auto_config", context.CorrelationId, cancellationToken);
            return result;
        }

        var defaultDescription = action is null
            ? $"Execute {toolName} (Risk: {riskLevel})"
            : $"Execute {toolName}.{action} (Risk: {riskLevel})";
        return await ConfirmAndPublishAsync(toolName, riskLevel, defaultDescription, context, parameters, cancellationToken);
    }

    private async Task<ConfirmationResult> ConfirmAndPublishAsync(
        string toolName,
        SecurityRiskLevel riskLevel,
        string description,
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken,
        string? detailsPrefix = null)
    {
        var confirmRequest = new ConfirmationRequest(
            toolName,
            description,
            riskLevel.ToString(),
            context.CorrelationId,
            parameters.ToDictionary(p => p.Key, p => p.Value));

        _logger.LogInformation("[SecurityManager] Requesting confirmation for {RiskLevel} tool: {ToolName}", riskLevel, toolName);

        var confirmation = await _confirmationService.RequestConfirmationAsync(confirmRequest, cancellationToken);

        var eventResult = confirmation.Confirmed ? "confirmed" : "denied";
        var details = $"Method: {confirmation.Method}, Response time: {confirmation.ResponseTime.TotalMilliseconds:F0}ms";
        await PublishSecurityEventAsync(toolName, riskLevel, confirmation.Confirmed, eventResult, context.CorrelationId, cancellationToken,
            detailsPrefix is null ? details : $"{detailsPrefix}; {details}");

        return confirmation;
    }

    private static string BuildN3Description(string toolName, string? action)
    {
        if (toolName.Equals("power", StringComparison.OrdinalIgnoreCase))
        {
            return action switch
            {
                "shutdown" => "Éteindre complètement le PC (délai de 30 secondes, annulable)",
                "restart" => "Redémarrer le PC (délai de 30 secondes, annulable)",
                _ => $"Action critique sur l'alimentation du PC : {action}",
            };
        }
        return $"Action critique : {toolName}.{action}";
    }

    private bool CheckRateLimit()
    {
        var now = DateTime.UtcNow;
        if ((now - _windowStart).TotalMinutes >= 1)
        {
            _actionCounts.Clear();
            _windowStart = now;
        }

        var key = "global";
        if (!_actionCounts.ContainsKey(key))
            _actionCounts[key] = 0;

        _actionCounts[key]++;

        return _actionCounts[key] > _options.MaxActionsPerMinute;
    }

    private async Task PublishSecurityEventAsync(string toolName, SecurityRiskLevel riskLevel, bool confirmed, string result, Guid correlationId, CancellationToken cancellationToken, string? details = null)
    {
        try
        {
            var securityEvent = new SecurityEvent(
                $"tool_{result}",
                toolName,
                riskLevel,
                confirmed,
                result,
                correlationId,
                details);

            await _eventBus.PublishAsync(securityEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SecurityManager] Failed to publish security event");
        }
    }
}
