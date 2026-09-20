namespace JarvisAI.Application.Overlay;

/// <summary>
/// Represents a piece of content to display on the AR overlay.
/// </summary>
public sealed class OverlayContent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public string? Icon { get; set; }
    public string Position { get; set; } = "top-right"; // top-left, top-center, top-right, bottom-left, bottom-right
    public int DurationMs { get; set; } = 5000; // 0 = permanent until dismissed
    public string Style { get; set; } = "default"; // default, alert, success, info
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// AR overlay service: displays information on top of all applications.
/// Uses a transparent, click-through window that's always on top.
/// </summary>
public interface IOverlayService
{
    /// <summary>
    /// Check if the overlay is running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Start the overlay window.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop the overlay window.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Show a piece of content on the overlay.
    /// </summary>
    void Show(OverlayContent content);

    /// <summary>
    /// Show a quick toast message.
    /// </summary>
    void Toast(string message, string style = "default", int durationMs = 3000);

    /// <summary>
    /// Show system stats (CPU, RAM, network).
    /// </summary>
    void ShowSystemStats(float cpuPercent, float ramPercent, string? networkInfo = null);

    /// <summary>
    /// Dismiss all overlay content.
    /// </summary>
    void Clear();
}
