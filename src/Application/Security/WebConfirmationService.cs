using JarvisAI.Application.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Application.Security;

public sealed class WebConfirmationService : IUserConfirmationService
{
    private readonly IConfirmationStore _store;
    private readonly ILogger<WebConfirmationService> _logger;
    private readonly TimeSpan _timeout;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public event Func<ConfirmationRequest, Task>? OnConfirmationRequested;

    public WebConfirmationService(IConfirmationStore store, ILogger<WebConfirmationService> logger, SecurityOptions? options = null)
    {
        _store = store;
        _logger = logger;
        var seconds = options?.DangerousToolTimeoutSeconds ?? 0;
        _timeout = seconds > 0 ? TimeSpan.FromSeconds(seconds) : DefaultTimeout;
    }

    public async Task<ConfirmationResult> RequestConfirmationAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[WebConfirmation] Requesting confirmation for tool: {ToolName} (Risk: {RiskLevel}, Timeout: {TimeoutSeconds}s)",
            request.ToolName, request.RiskLevel, _timeout.TotalSeconds);

        var requestId = await _store.StoreRequestAsync(request, cancellationToken);

        if (OnConfirmationRequested != null)
        {
            try { await OnConfirmationRequested.Invoke(request); }
            catch (Exception ex) { _logger.LogWarning(ex, "[WebConfirmation] Error notifying clients"); }
        }

        var result = await _store.WaitForResolutionAsync(requestId, _timeout, cancellationToken);

        _logger.LogInformation("[WebConfirmation] Resolution for {ToolName}: {Confirmed} (Method={Method})",
            request.ToolName, result.Confirmed, result.Method);

        return result;
    }
}
