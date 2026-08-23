using JarvisAI.Application.Agents;
using JarvisAI.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Hubs;

public sealed class AgentHub : Hub
{
    private readonly IRunHistory _runHistory;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(IRunHistory runHistory, ILogger<AgentHub> logger)
    {
        _runHistory = runHistory;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("[AgentHub] Client connected: {ConnectionId}", Context.ConnectionId);
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[AgentHub] Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinRun(Guid runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(runId));
    }

    public async Task LeaveRun(Guid runId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(runId));
    }

    public IReadOnlyList<AgentRunDto> GetRuns(int count = 20)
        => _runHistory.GetRecent(count).Select(AgentRunDto.From).ToList();

    public AgentRunDto? GetRun(Guid runId)
    {
        var run = _runHistory.Get(runId);
        return run is null ? null : AgentRunDto.From(run);
    }

    internal static string GroupName(Guid runId) => $"run:{runId:N}";
}
