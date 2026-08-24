using JarvisAI.Application.WebAutomation;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace JarvisAI.Infrastructure.WebAutomation;

public sealed class PlaywrightWebBrowser : IWebBrowser
{
    private readonly ILogger<PlaywrightWebBrowser> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly bool _headless;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    public PlaywrightWebBrowser(ILogger<PlaywrightWebBrowser> logger, bool headless = false)
    {
        _logger = logger;
        _headless = headless;
    }

    public bool IsAvailable => true;

    public IPage? GetPage() => _page;

    // ── Gestion des onglets (assistant navigateur, façon tools/navigateur.py) ──
    public IReadOnlyList<IPage> GetPages() =>
        _context?.Pages.Where(p => !p.IsClosed).ToList() ?? new List<IPage>();

    public async Task<IPage?> NewTabAsync(string? url = null, CancellationToken ct = default)
    {
        if (!await LaunchAsync(ct)) return null;
        var page = await _context!.NewPageAsync();
        _page = page; // l'onglet neuf devient l'onglet actif
        if (!string.IsNullOrWhiteSpace(url))
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });
        return page;
    }

    public async Task<bool> FocusTabAsync(int index, CancellationToken ct = default)
    {
        var pages = GetPages();
        if (index < 0 || index >= pages.Count) return false;
        _page = pages[index];
        try { await _page.BringToFrontAsync(); } catch { }
        return true;
    }

    public async Task<bool> CloseTabAsync(int index, CancellationToken ct = default)
    {
        var pages = GetPages();
        if (index < 0 || index >= pages.Count) return false;
        try
        {
            await pages[index].CloseAsync();
            if (_page == pages[index])
                _page = pages.Count > 1 ? pages[pages.Count - 1] : null;
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> LaunchAsync(CancellationToken cancellationToken = default)
    {
        if (_page is not null && !_page.IsClosed)
            return true;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_page is not null && !_page.IsClosed)
                return true;

            _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();

            // Profil dédié PERSISTANT (façon repo Python reservation.py) : les
            // connexions aux sites (Amazon, YouTube...) sont conservées entre les
            // sessions, sans jamais toucher au Chrome quotidien de l'utilisateur.
            // Surcharge possible via JARVIS_BROWSER_PROFILE (isolation des tests).
            var userDataDir = Environment.GetEnvironmentVariable("JARVIS_BROWSER_PROFILE");
            if (string.IsNullOrWhiteSpace(userDataDir))
            {
                userDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChromeJarvis");
            }
            Directory.CreateDirectory(userDataDir);

            // Auto-réparation : un profil verrouillé/corrompu fait mourir le
            // contexte dès sa création (TargetClosedException). On met le
            // profil fautif de côté et on repart sur un profil neuf.
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    _context ??= await LaunchContextAsync(userDataDir);
                    _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
                    break;
                }
                catch (Exception ex) when (attempt == 1 && ex.GetType().Name == "TargetClosedException")
                {
                    _logger.LogWarning("[PlaywrightWebBrowser] Contexte fermé à l'init — profil {Dir} mis de côté, nouvel essai", userDataDir);
                    try { _context?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
                    _context = null;
                    _page = null;
                    var quarantined = userDataDir + ".bad-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    try { Directory.Move(userDataDir, quarantined); } catch { /* profil re-créé à chaud */ }
                    Directory.CreateDirectory(userDataDir);
                }
            }

            if (_page is null || _page.IsClosed)
                return false;

            await _page.Context.AddCookiesAsync(new[]
            {
                new Cookie
                {
                    Name = "CONSENT",
                    Value = "YES+cb.20240101-00-p0.en+FX+999",
                    Domain = ".youtube.com",
                    Path = "/"
                },
                new Cookie
                {
                    Name = "SOCS",
                    Value = "CAISNQgDEitib3FfaWRlbnRpdHlmcm9udGVuZHVpc2VydmVyXzIwMjQwMTAxLjA3X3AxGgJlbiACGgYIgJnsBQ",
                    Domain = ".youtube.com",
                    Path = "/"
                }
            });
            _logger.LogInformation("[PlaywrightWebBrowser] Navigateur lancé");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaywrightWebBrowser] Échec du lancement du navigateur");
            return false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Lance le contexte persistant ; tente Chrome puis bascule sur
    /// Edge si Chrome n'est pas disponible sur la machine.</summary>
    private async Task<IBrowserContext> LaunchContextAsync(string userDataDir)
    {
        try
        {
            return await _playwright!.Chromium.LaunchPersistentContextAsync(userDataDir, BuildLaunchOptions("chrome"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PlaywrightWebBrowser] Chrome indisponible — bascule sur Edge");
            return await _playwright!.Chromium.LaunchPersistentContextAsync(userDataDir, BuildLaunchOptions("msedge"));
        }
    }

    private BrowserTypeLaunchPersistentContextOptions BuildLaunchOptions(string channel) =>
        new()
        {
            Channel = channel,
            Headless = _headless,
            Args = new[] { "--disable-blink-features=AutomationControlled", "--start-maximized" },
            ViewportSize = ViewportSize.NoViewport,
            Locale = "fr-FR"
        };

    public async Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            url = "https://" + url;

        try
        {
            await _page!.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });
            await AcceptConsentIfPresentAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaywrightWebBrowser] Échec de navigation vers {Url}", url);
            return false;
        }
    }

    /// <summary>Si la page affiche le mur de consentement (YouTube, Google…),
    /// clique automatiquement « Tout accepter » pour ne pas bloquer le flux.</summary>
    private async Task AcceptConsentIfPresentAsync()
    {
        try
        {
            if (_page is null || !_page.Url.Contains("consent.")) return;
            foreach (var label in new[] { "Tout accepter", "Accept all", "Tout accepter " })
            {
                var bouton = _page.Locator($"button:has-text(\"{label}\")").First;
                if (await bouton.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 2000 }))
                {
                    await bouton.ClickAsync();
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    _logger.LogInformation("[PlaywrightWebBrowser] Consentement accepté automatiquement ({Label})", label);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PlaywrightWebBrowser] Pas de consentement à accepter");
        }
    }

    public async Task<string?> GetUrlAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return null;
        return _page!.Url;
    }

    public async Task<string?> GetTitleAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return null;
        try { return await _page!.TitleAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "[PlaywrightWebBrowser] Échec lecture titre"); return null; }
    }

    public async Task<string> GetTextAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return string.Empty;
        try { return await _page!.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''"); }
        catch (Exception ex) { _logger.LogError(ex, "[PlaywrightWebBrowser] Échec extraction texte"); return string.Empty; }
    }

    public async Task<WebPageSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return null;

        try
        {
            var url = _page!.Url;
            var title = await _page.TitleAsync();
            var text = await _page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''");
            string? screenshot = null;
            try
            {
                var bytes = await _page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
                screenshot = Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PlaywrightWebBrowser] Échec capture écran navigateur");
            }
            return new WebPageSnapshot(url, title, text, screenshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaywrightWebBrowser] Échec snapshot");
            return null;
        }
    }

    public async Task<bool> ClickAsync(string selector, CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return false;
        try
        {
            await _page!.ClickAsync(selector, new PageClickOptions { Timeout = 15_000 });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaywrightWebBrowser] Échec clic sur {Selector}", selector);
            return false;
        }
    }

    public async Task<bool> FillAsync(string selector, string text, CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return false;
        try
        {
            await _page!.FillAsync(selector, text);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaywrightWebBrowser] Échec remplissage {Selector}", selector);
            return false;
        }
    }

    public async Task<bool> TypeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return false;
        try
        {
            await _page!.Keyboard.InsertTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaywrightWebBrowser] Échec saisie texte");
            return false;
        }
    }

    public async Task<bool> PressAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return false;
        try
        {
            await _page!.Keyboard.PressAsync(key);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaywrightWebBrowser] Échec touche {Key}", key);
            return false;
        }
    }

    public async Task<bool> WaitForSelectorAsync(string selector, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return false;
        try
        {
            await _page!.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PlaywrightWebBrowser] Sélecteur absent: {Selector}", selector);
            return false;
        }
    }

    public async Task<bool> CloseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_page is not null)
            {
                await _page.CloseAsync();
                _page = null;
            }
            if (_context is not null)
            {
                await _context.CloseAsync();
                _context = null;
            }
            if (_browser is not null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }
            if (_playwright is not null)
            {
                _playwright.Dispose();
                _playwright = null;
            }
            _logger.LogInformation("[PlaywrightWebBrowser] Navigateur fermé");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaywrightWebBrowser] Échec fermeture navigateur");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<bool> EnsurePageAsync(CancellationToken cancellationToken)
    {
        if (_page is not null && !_page.IsClosed)
            return true;
        return await LaunchAsync(cancellationToken);
    }
}
