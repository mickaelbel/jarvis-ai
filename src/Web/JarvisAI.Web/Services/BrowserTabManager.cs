using JarvisAI.Infrastructure.WebAutomation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

public sealed class BrowserTabManager : IBrowserTabManager
{
    private readonly ILogger<BrowserTabManager> _logger;
    private readonly PlaywrightWebBrowser _browser;

    public BrowserTabManager(ILogger<BrowserTabManager> logger, PlaywrightWebBrowser browser)
    {
        _logger = logger;
        _browser = browser;
    }

    public async Task<IReadOnlyList<TabInfo>> GetTabsAsync(CancellationToken ct = default)
    {
        try
        {
            var pages = _browser.GetPages();
            var tabs = new List<TabInfo>();

            for (int i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var title = await page.TitleAsync();
                var url = page.Url;
                var isActive = i == pages.Count - 1;

                tabs.Add(new TabInfo(i, title, url, isActive, null));
            }

            return tabs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserTabManager] Failed to get tabs");
            return Array.Empty<TabInfo>();
        }
    }

    public async Task<TabInfo?> FocusTabAsync(int index, CancellationToken ct = default)
    {
        try
        {
            var success = await _browser.FocusTabAsync(index);
            if (!success) return null;

            var pages = _browser.GetPages();
            if (index < 0 || index >= pages.Count) return null;

            var page = pages[index];
            var title = await page.TitleAsync();
            return new TabInfo(index, title, page.Url, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserTabManager] Failed to focus tab {Index}", index);
            return null;
        }
    }

    public async Task<TabInfo?> CloseTabAsync(int index, CancellationToken ct = default)
    {
        try
        {
            var success = await _browser.CloseTabAsync(index);
            if (!success) return null;

            return new TabInfo(index, "Closed", "", false, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserTabManager] Failed to close tab {Index}", index);
            return null;
        }
    }

    public async Task<TabInfo?> NewTabAsync(string? url = null, CancellationToken ct = default)
    {
        try
        {
            var page = await _browser.NewTabAsync(url);
            if (page is null) return null;

            var title = await page.TitleAsync();
            var pages = _browser.GetPages();
            var index = pages.ToList().IndexOf(page);

            return new TabInfo(index, title, page.Url, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserTabManager] Failed to create new tab");
            return null;
        }
    }

    public async Task<string?> GetScreenshotAsync(int index, CancellationToken ct = default)
    {
        try
        {
            var pages = _browser.GetPages();
            if (index < 0 || index >= pages.Count) return null;

            var page = pages[index];
            var screenshot = await page.ScreenshotAsync();
            return Convert.ToBase64String(screenshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserTabManager] Failed to screenshot tab {Index}", index);
            return null;
        }
    }
}
