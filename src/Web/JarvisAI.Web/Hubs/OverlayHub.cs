using Microsoft.AspNetCore.SignalR;

namespace JarvisAI.Web.Hubs;

/// <summary>
/// Diffuse les réponses de Jarvis vers l'overlay flottant du bureau (WPF).
/// Le Desktop s'y connecte en client local ; Chat.razor pousse les réponses finales.
/// </summary>
public sealed class OverlayHub : Hub
{
    public Task Join() => Groups.AddToGroupAsync(Context.ConnectionId, "desktop");

    // ── Debug overlay methods ─────────────────────────────────────────────

    public Task DebugLog(string message) =>
        Clients.Group("desktop").SendAsync("debugLog", message);

    public Task DebugLogCategory(string message, string category) =>
        Clients.Group("desktop").SendAsync("debugLogCategory", message, category);

    public Task DebugThinking(string thought) =>
        Clients.Group("desktop").SendAsync("debugThinking", thought);

    public Task DebugFileChange(string change) =>
        Clients.Group("desktop").SendAsync("debugFileChange", change);

    public Task DebugResponse(string response) =>
        Clients.Group("desktop").SendAsync("debugResponse", response);

    // ── Image/Video overlay methods ───────────────────────────────────────

    public Task ShowImage(string path, double? x, double? y, double? width) =>
        Clients.Group("desktop").SendAsync("overlayImage", path, x, y, width);

    public Task ShowVideo(string path, double? x, double? y, double? width) =>
        Clients.Group("desktop").SendAsync("overlayVideo", path, x, y, width);

    public Task ShowSlideshow(string[] paths, int intervalMs, double? x, double? y) =>
        Clients.Group("desktop").SendAsync("overlaySlideshow", paths, intervalMs, x, y);

    public Task CloseImage() =>
        Clients.Group("desktop").SendAsync("overlayCloseImage");
}
