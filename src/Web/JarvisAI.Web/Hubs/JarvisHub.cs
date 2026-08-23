using JarvisAI.Application.Security;
using JarvisAI.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Hubs;

public sealed class JarvisHub : Hub
{
    private readonly ILogger<JarvisHub> _logger;
    private readonly IConfirmationStore _confirmationStore;

    public JarvisHub(ILogger<JarvisHub> logger, IConfirmationStore confirmationStore)
    {
        _logger = logger;
        _confirmationStore = confirmationStore;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("[JarvisHub] Client connected: {ConnectionId}", Context.ConnectionId);
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[JarvisHub] Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SendMessage(string message)
    {
        _logger.LogInformation("[JarvisHub] Message from {ConnectionId}: {Message}", Context.ConnectionId, message);
        await Clients.All.SendAsync("ReceiveMessage", Context.ConnectionId, message);
    }

    public async Task ConfirmAction(Guid requestId, bool confirmed)
    {
        _logger.LogInformation("[JarvisHub] Confirmation from {ConnectionId}: RequestId={RequestId}, Confirmed={Confirmed}",
            Context.ConnectionId, requestId, confirmed);

        var method = ConfirmationMethod.Text;
        var result = confirmed
            ? ConfirmationResult.Accepted(method, TimeSpan.Zero, "web_confirmed")
            : ConfirmationResult.Denied(method, TimeSpan.Zero, "web_denied");

        await _confirmationStore.ResolveRequestAsync(requestId, result);
        await Clients.All.SendAsync("ConfirmationResolved", requestId, confirmed);
    }
}
