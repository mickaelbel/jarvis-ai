namespace JarvisAI.Application.WebAutomation;

public sealed record WebPageSnapshot(string Url, string Title, string Text, string? ScreenshotBase64);

public interface IWebBrowser : IAsyncDisposable
{
    bool IsAvailable { get; }

    Task<bool> LaunchAsync(CancellationToken cancellationToken = default);
    Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default);
    Task<string?> GetUrlAsync(CancellationToken cancellationToken = default);
    Task<string?> GetTitleAsync(CancellationToken cancellationToken = default);
    Task<string> GetTextAsync(CancellationToken cancellationToken = default);
    Task<WebPageSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default);
    Task<bool> ClickAsync(string selector, CancellationToken cancellationToken = default);
    Task<bool> FillAsync(string selector, string text, CancellationToken cancellationToken = default);
    Task<bool> TypeAsync(string text, CancellationToken cancellationToken = default);
    Task<bool> PressAsync(string key, CancellationToken cancellationToken = default);
    Task<bool> WaitForSelectorAsync(string selector, int timeoutMs, CancellationToken cancellationToken = default);
    Task<bool> CloseAsync(CancellationToken cancellationToken = default);
}
