using JarvisAI.Infrastructure.WebAutomation;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class PlaywrightWebBrowserTests
{
    private static PlaywrightWebBrowser Create(bool headless = true)
    {
        // Profil isolé par test : ne jamais entrer en conflit avec le profil
        // de l'application qui tourne (%LOCALAPPDATA%\ChromeJarvis).
        Environment.SetEnvironmentVariable("JARVIS_BROWSER_PROFILE",
            Path.Combine(Path.GetTempPath(), "jarvis-test-profile-" + Guid.NewGuid().ToString("N")));
        return new PlaywrightWebBrowser(NullLogger<PlaywrightWebBrowser>.Instance, headless);
    }

    [Fact]
    public async Task Launch_and_navigate_to_data_url_returns_text()
    {
        await using var browser = Create();
        Assert.True(browser.IsAvailable);

        var launched = await browser.LaunchAsync();
        Assert.True(launched);

        var ok = await browser.NavigateAsync("data:text/html,<html><body><h1>Hello Playwright Jarvis</h1></body></html>");
        Assert.True(ok);

        var url = await browser.GetUrlAsync();
        Assert.NotNull(url);
        Assert.StartsWith("data:text/html", url);

        var text = await browser.GetTextAsync();
        Assert.Contains("Hello Playwright Jarvis", text);

        var title = await browser.GetTitleAsync();
        Assert.NotNull(title);
    }

    [Fact]
    public async Task Snapshot_returns_url_title_and_text()
    {
        await using var browser = Create();
        Assert.True(await browser.LaunchAsync());
        Assert.True(await browser.NavigateAsync("data:text/html,<html><body><p>Snapshot content check</p></body></html>"));

        var snapshot = await browser.SnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.StartsWith("data:text/html", snapshot.Url);
        Assert.Contains("Snapshot content check", snapshot.Text);
    }

    [Fact]
    public async Task Click_and_wait_selector_work_on_dom()
    {
        await using var browser = Create();
        Assert.True(await browser.LaunchAsync());

        const string html = "<html><body><button id='b' onclick=\"document.getElementById('out').innerText='clicked'\">Go</button><p id='out'></p></body></html>";
        Assert.True(await browser.NavigateAsync("data:text/html," + html));

        Assert.True(await browser.WaitForSelectorAsync("#b", 5_000));
        Assert.True(await browser.ClickAsync("#b"));

        var text = await browser.GetTextAsync();
        Assert.Contains("clicked", text);
    }

    [Fact]
    public async Task Close_returns_true()
    {
        var browser = Create();
        Assert.True(await browser.LaunchAsync());
        Assert.True(await browser.CloseAsync());
    }

    [Fact]
    public async Task Operations_auto_launch_and_report_blank_page()
    {
        await using var browser = Create();
        var text = await browser.GetTextAsync();
        Assert.Equal(string.Empty, text);

        var snapshot = await browser.SnapshotAsync();
        Assert.NotNull(snapshot);
        Assert.Equal("about:blank", snapshot.Url);
    }
}
