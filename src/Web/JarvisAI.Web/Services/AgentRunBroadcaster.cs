using JarvisAI.Application.Agents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public sealed class AgentRunBroadcaster : IDisposable
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IRunHistory _runHistory;
    private readonly IHubContext<Hubs.AgentHub> _hubContext;
    private readonly ILogger<AgentRunBroadcaster> _logger;

    public AgentRunBroadcaster(
        IAgentOrchestrator orchestrator,
        IRunHistory runHistory,
        IHubContext<Hubs.AgentHub> hubContext,
        ILogger<AgentRunBroadcaster> logger)
    {
        _orchestrator = orchestrator;
        _runHistory = runHistory;
        _hubContext = hubContext;
        _logger = logger;
    }

    public void Start()
    {
        _orchestrator.RunUpdated += OnRunUpdated;
        _logger.LogInformation("[AgentRunBroadcaster] Started listening to run updates");
    }

    public void Dispose()
    {
        _orchestrator.RunUpdated -= OnRunUpdated;
    }

    private async void OnRunUpdated(object? sender, AgentRunUpdatedEventArgs args)
    {
        try
        {
            var run = _runHistory.Get(args.RunId);
            if (run is null) return;

            var dto = AgentRunDto.From(run);
            await _hubContext.Clients.Group(Hubs.AgentHub.GroupName(run.RunId))
                .SendAsync("RunUpdated", dto);
            await _hubContext.Clients.All
                .SendAsync("RunListChanged", dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentRunBroadcaster] Failed to broadcast run update for {RunId}", args.RunId);
        }
    }
}
