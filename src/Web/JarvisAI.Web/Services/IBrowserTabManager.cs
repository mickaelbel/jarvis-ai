namespace JarvisAI.Web.Services;

/// <summary>
/// Gestionnaire d'onglets browser avec miniatures.
/// </summary>
public interface IBrowserTabManager
{
    Task<IReadOnlyList<TabInfo>> GetTabsAsync(CancellationToken ct = default);
    Task<TabInfo?> FocusTabAsync(int index, CancellationToken ct = default);
    Task<TabInfo?> CloseTabAsync(int index, CancellationToken ct = default);
    Task<TabInfo?> NewTabAsync(string? url = null, CancellationToken ct = default);
    Task<string?> GetScreenshotAsync(int index, CancellationToken ct = default);
}

public sealed record TabInfo(
    int Index,
    string Title,
    string Url,
    bool IsActive,
    string? ScreenshotBase64);
