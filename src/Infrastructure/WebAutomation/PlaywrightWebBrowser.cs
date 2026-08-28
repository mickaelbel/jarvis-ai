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
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        try { await page.BringToFrontAsync(); } catch { }
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
            var toRemove = _namedPages.Where(kv => kv.Value == pages[index]).Select(kv => kv.Key).ToList();
            foreach (var k in toRemove) _namedPages.Remove(k);
            return true;
        }
        catch { return false; }
    }

    // ── Onglets nommés : permet de piloter PLUSIEURS onglets distincts (chacun
    // une « tâche ») sans que l'un écrase l'autre. Le nom sert de poignée stable :
    // l'agent assigne un nom à un onglet, travaille dessus, y revient, etc. ──────
    private readonly Dictionary<string, IPage> _namedPages = new();

    public IReadOnlyDictionary<string, IPage> NamedPages => _namedPages;

    public async Task<IPage?> NewTabNamedAsync(string name, string? url = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return await NewTabAsync(url, ct);
        _namedPages.TryGetValue(name, out var existing);
        if (existing is not null && !existing.IsClosed) return existing;

        if (!await LaunchAsync(ct)) return null;
        var page = await _context!.NewPageAsync();
        _namedPages[name] = page;
        _page = page; // le nouvel onglet devient l'onglet actif
        if (!string.IsNullOrWhiteSpace(url))
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        try { await page.BringToFrontAsync(); } catch { }
        return page;
    }

    public async Task<IPage?> GetNamedPageAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return _page;
        _namedPages.TryGetValue(name, out var page);
        if (page is null || page.IsClosed) return null;
        _page = page; // on fait de cet onglet l'actif
        try { await page.BringToFrontAsync(); } catch { }
        return page;
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

            // Mode application : on se CONNECTE au Chrome visible de
            // l'utilisateur via CDP (façon tools/navigateur.py du projet
            // jarvis-assistant-vocal qui marche). Plus jamais de second
            // navigateur ni de conflit de profil : c'est SON Chrome.
            if (!_headless)
            {
                if (await LaunchViaCdpAsync(cancellationToken))
                    return true;
                if (_userChromeRunning)
                {
                    // SON Chrome tourne déjà (sans port CDP) : on ne lance PAS un
                    // 2e profil « bizarre ». On laisse l'appelant ouvrir l'URL
                    // directement dans Chrome (façon lien QuickShare).
                    _logger.LogInformation("[PlaywrightWebBrowser] Chrome utilisateur déjà ouvert — pas de repli ChromeJarvis, ouverture directe");
                    return false;
                }
                _logger.LogWarning("[PlaywrightWebBrowser] CDP indisponible — repli sur lancement dédié interne");
            }

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
            // Cas fréquent : un chrome.exe zombie (session précédente killée)
            // squatte encore le dossier → on le termine d'abord, sinon chaque
            // lancement délègue au zombie et meurt instantanément.
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
                    KillChromeHoldingProfile(userDataDir);
                    var quarantined = userDataDir + ".bad-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    try
                    {
                        Directory.Move(userDataDir, quarantined);
                        Directory.CreateDirectory(userDataDir);
                    }
                    catch
                    {
                        // Dossier encore verrouillé par un processus tiers :
                        // on part sur un chemin alternatif plutôt que de retenter
                        // indéfiniment sur le même profil mort.
                        userDataDir += "-alt-" + DateTime.UtcNow.ToString("HHmmss");
                        Directory.CreateDirectory(userDataDir);
                        _logger.LogWarning("[PlaywrightWebBrowser] Profil verrouillé — bascule sur {Dir}", userDataDir);
                    }
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

    // ── Mode CDP partagé : le Chrome VISIBLE de l'utilisateur ────────────────
    private const int CdpPort = 9222;
    private IBrowser? _cdpBrowser;
    private bool _userChromeRunning;

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            var ar = c.BeginConnect("127.0.0.1", port, null, null);
            if (ar.AsyncWaitHandle.WaitOne(400) && c.Connected) { c.EndConnect(ar); return true; }
            return false;
        }
        catch { return false; }
    }

    private static string? FindChromeExe()
    {
        foreach (var baseDir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            var p = Path.Combine(baseDir, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string SharedProfileDir()
    {
        // Profil RÉEL de l'utilisateur : on ouvre/contrôle SON Chrome (profil,
        // comptes, historique), exactement comme s'il cliquait sur un lien.
        // Surcharge possible via JARVIS_BROWSER_PROFILE (isolation des tests).
        var dir = Environment.GetEnvironmentVariable("JARVIS_BROWSER_PROFILE");
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var real = Path.Combine(localAppData, "Google", "Chrome", "User Data");
        return Directory.Exists(real) ? real : Path.Combine(localAppData, "ChromeJarvis");
    }

    /// <summary>Un chrome.exe tourne-t-il déjà avec ce profil (command line
    /// contient le user-data-dir) ? Si oui on ne peut PAS le rattacher sans port
    /// de débogage → on basculera sur une simple ouverture dans le navigateur.</summary>
    private static bool IsChromeUsingProfile(string profile)
    {
        try
        {
            var like = profile.Replace("'", "''");
            var script = "Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" | " +
                         "Where-Object { $_.CommandLine -like '*" + like + "*' } | " +
                         "Measure-Object | Select-Object -ExpandProperty Count";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            var outText = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(5000);
            return int.TryParse(outText, out var n) && n > 0;
        }
        catch { return false; }
    }

    /// <summary>Se connecte au Chrome lancé avec --remote-debugging-port=9222 ;
    /// si aucun n'écoute, lance chrome.exe (profil dédié persistant, fenêtre
    /// visible maximisée — l'utilisateur surfe dedans comme d'habitude, ses
    /// connexions restent mémorisées) puis s'y connecte.</summary>
    private async Task<bool> LaunchViaCdpAsync(CancellationToken ct)
    {
        _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();

        if (_cdpBrowser?.IsConnected != true)
        {
            if (!IsPortOpen(CdpPort))
            {
                var chrome = FindChromeExe();
                if (chrome is null)
                {
                    _logger.LogWarning("[PlaywrightWebBrowser] chrome.exe introuvable pour le mode CDP");
                    return false;
                }
                var profile = SharedProfileDir();
                Directory.CreateDirectory(profile);

                // Si SON Chrome (vrai profil) tourne déjà sans port de débogage, on
                // ne peut pas le rattacher : on ne lance pas un 2e profil « bizarre ».
                // On revient false → l'appelant ouvre simplement l'URL dans son Chrome
                // (façon lien QuickShare). Sinon on lance SON profil avec le port CDP.
                if (IsChromeUsingProfile(profile))
                {
                    _logger.LogInformation("[PlaywrightWebBrowser] Chrome utilisateur déjà ouvert (profil {Profile}) sans port CDP — on ouvrira l'URL directement", profile);
                    _userChromeRunning = true;
                    return false;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = chrome,
                    Arguments = $"--remote-debugging-port={CdpPort} --user-data-dir=\"{profile}\" --start-maximized",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                for (var i = 0; i < 16 && !IsPortOpen(CdpPort); i++)
                    await Task.Delay(500, ct);
                _logger.LogInformation("[PlaywrightWebBrowser] Chrome partagé lancé (port {Port}, profil {Profile})", CdpPort, profile);
            }

            try
            {
                _cdpBrowser = await _playwright.Chromium.ConnectOverCDPAsync(
                    $"http://127.0.0.1:{CdpPort}",
                    new BrowserTypeConnectOverCDPOptions { Timeout = 6000 });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PlaywrightWebBrowser] Connexion CDP échouée sur {Port}", CdpPort);
                return false;
            }
        }

        _browser = _cdpBrowser;
        _context = _cdpBrowser.Contexts.FirstOrDefault() ?? await _cdpBrowser.NewContextAsync();
        _page = await SelectActivePageAsync(_context) ?? await _context.NewPageAsync();
        if (_page is null || _page.IsClosed)
            return false;

        try
        {
            await _page.Context.AddCookiesAsync(new[]
            {
                new Cookie { Name = "CONSENT", Value = "YES+cb.20240101-00-p0.en+FX+999", Domain = ".youtube.com", Path = "/" },
                new Cookie { Name = "SOCS", Value = "CAISNQgDEitib3FfaWRlbnRpdHlmcm9udGVuZHVpc2VydmVyXzIwMjQwMTAxLjA3X3AxGgJlbiACGgYIgJnsBQ", Domain = ".youtube.com", Path = "/" }
            });
        }
        catch { /* best-effort */ }

        _logger.LogInformation("[PlaywrightWebBrowser] Connecté au Chrome de l'utilisateur (CDP:{Port}, {Pages} onglet(s))", CdpPort, _context.Pages.Count);
        return true;
    }

    private static async Task<IPage?> SelectActivePageAsync(IBrowserContext context)
    {
        IPage? lastAlive = null;
        IPage? firstVisible = null;
        foreach (var p in context.Pages)
        {
            if (p.IsClosed) continue;
            lastAlive ??= p;
            try
            {
                var vis = await p.EvaluateAsync<string>("() => document.visibilityState");
                if (vis == "visible")
                {
                    try
                    {
                        var focus = await p.EvaluateAsync<bool>("() => document.hasFocus()");
                        if (focus) return p;
                    }
                    catch { }
                    firstVisible ??= p;
                }
            }
            catch { }
        }
        return firstVisible ?? lastAlive;
    }

    /// <summary>Termine les chrome.exe qui utilisent encore notre profil dédié
    /// (zombies d'une session précédemment killée). Best-effort, via PowerShell
    /// pour accéder aux lignes de commande sans dépendance WMI.</summary>
    private static void KillChromeHoldingProfile(string userDataDir)
    {
        try
        {
            var like = userDataDir.Replace("'", "''");
            var script = "Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" | " +
                         "Where-Object { $_.CommandLine -like '*" + like + "*' } | " +
                         "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch
        {
            // best-effort : le retry / dossier alternatif prendra le relais
        }
    }

    /// <summary>Lance le contexte persistant ; tente Chrome puis bascule sur
    /// Edge si Chrome n'est pas disponible sur la machine.</summary>
    private async Task<IBrowserContext> LaunchContextAsync(string userDataDir)    {
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

        // Mode CDP : on navigue dans l'onglet que l'utilisateur regarde
        // (façon navigateur.py : la page active est le point d'ancrage).
        if (_cdpBrowser?.IsConnected == true && _context is not null)
        {
            var active = await SelectActivePageAsync(_context);
            if (active is not null) _page = active;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            url = "https://" + url;

        try
        {
            // DOMContentLoaded (et non pas Load) : revient dès que le HTML/JS est
            // prêt, sans attendre toutes les images/fonts → navigation quasi
            // instantanée. Le texte/les clics marchent dès ce stade.
            await _page!.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
            await AcceptConsentIfPresentAsync();
            try { await _page.BringToFrontAsync(); } catch { }
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

    public async Task<WebPageSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default, bool includeScreenshot = false)
    {
        if (!await EnsurePageAsync(cancellationToken))
            return null;

        try
        {
            var url = _page!.Url;
            var title = await _page.TitleAsync();
            var text = await _page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''");
            string? screenshot = null;
            // La capture PNG complète est coûteuse (encode + base64) : on ne la
            // fait que si un appelant en a réellement besoin, jamais par défaut.
            if (includeScreenshot)
            {
                try
                {
                    var bytes = await _page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
                    screenshot = Convert.ToBase64String(bytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PlaywrightWebBrowser] Échec capture écran navigateur");
                }
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
            // Mode CDP : on DÉCONNECTE seulement — le Chrome de l'utilisateur
            // reste ouvert avec tous ses onglets.
            if (_cdpBrowser is not null)
            {
                _page = null;
                _context = null;
                _browser = null;
                var cdp = _cdpBrowser;
                _cdpBrowser = null;
                await cdp.CloseAsync(); // déconnexion CDP, Chrome continue de vivre
                _logger.LogInformation("[PlaywrightWebBrowser] Déconnecté du Chrome partagé");
                return true;
            }

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
