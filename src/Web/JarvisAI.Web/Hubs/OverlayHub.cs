using Microsoft.AspNetCore.SignalR;

namespace JarvisAI.Web.Hubs;

/// <summary>
/// Diffuse les réponses de Jarvis vers l'overlay flottant du bureau (WPF).
/// Le Desktop s'y connecte en client local ; Chat.razor pousse les réponses finales.
/// </summary>
public sealed class OverlayHub : Hub
{
    public Task Join() => Groups.AddToGroupAsync(Context.ConnectionId, "desktop");
}
