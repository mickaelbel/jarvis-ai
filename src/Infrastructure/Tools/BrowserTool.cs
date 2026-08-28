using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Tools;
using JarvisAI.Application.WebAutomation;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.WebAutomation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using System.Diagnostics;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class BrowserTool : ITool
{
    private readonly ILogger<BrowserTool> _logger;
    private readonly BrowserManager _browserManager;
    private readonly IWebBrowser? _webBrowser;
    private readonly IComputerController? _computer;
    private static readonly HttpClient _httpClient = BuildHttpClient();

    // Port du _JS_ELEMENTS du repo Python : tag chaque élément interactif avec
    // data-jaridx dans le DOM et retourne sa liste numérotée (index stable pour
    // click_index / fill_index — plus jamais de sélecteur CSS deviné).
    private const string JsTagElements = @"() => {
      const sel = 'a,button,input,textarea,select,[role=button],[role=link],[role=option],[role=menuitem],[role=combobox]';
      const out = [];
      let i = 0;
      for (const el of document.querySelectorAll(sel)) {
        const r = el.getBoundingClientRect();
        if (r.width < 2 || r.height < 2) continue;
        const st = getComputedStyle(el);
        if (st.visibility === 'hidden' || st.display === 'none' || st.opacity === '0') continue;
        el.setAttribute('data-jaridx', i);
        let label = (el.innerText || el.value || el.placeholder ||
                     el.getAttribute('aria-label') || el.getAttribute('name') || '').trim();
        out.push({
          index: i,
          tag: el.tagName.toLowerCase(),
          type: (el.getAttribute('type') || '').toLowerCase(),
          label: label.slice(0, 90),
          inView: r.top >= -5 && r.top < (window.innerHeight + 5)
        });
        i++;
      }
      return out;
    }";

    private static HttpClient BuildHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7");
        return client;
    }

    public string Name => "browser";
    public string Description =>
        "Contrôle complet du navigateur de l'utilisateur. " +
        "Actions: open_url, navigate, view (liste les éléments numérotés), get_elements, extract, snapshot, click_index, fill_index, click, click_at, fill, type, press, hold, scroll, screenshot, send_keys, list_tabs, new_tab, focus_tab, close_tab, site_search, parallel_search, youtube_latest, youtube_search, list_windows, focus, close_browser.";
    public string Category => "browser";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;
    public string? WaitingPhrase => "J'ouvre ça dans ton navigateur.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action",   "site_search | open_url | navigate | view | click_index | fill_index | click | click_at | fill | type | press | hold | scroll | screenshot | get_elements | extract | snapshot | send_keys | list_tabs | new_tab | focus_tab | close_tab | list_windows | focus | youtube_latest | youtube_search | close_browser", typeof(string), required: true),
        new ToolParameter("url",      "URL à ouvrir/naviguer, ou page d'accueil du site (site_search)", typeof(string)),
        new ToolParameter("channel",  "Nom de la chaîne YouTube (youtube_latest). Ex: MrBeast", typeof(string)),
        new ToolParameter("selector", "Sélecteur CSS (click, fill, get_elements)", typeof(string)),
        new ToolParameter("text",     "Texte à saisir (fill, type, fill_index)", typeof(string)),
        new ToolParameter("key",      "Touche clavier (press, hold, send_keys). Ex: Space, Enter, F, ctrl+c", typeof(string)),
        new ToolParameter("x",        "Coordonnée X en pixels (click_at)", typeof(string)),
        new ToolParameter("y",        "Coordonnée Y en pixels (click_at)", typeof(string)),
        new ToolParameter("direction", "Haut ou bas (scroll). Valeurs: up, down, page_up, page_down", typeof(string)),
        new ToolParameter("duration_ms", "Durée en millisecondes (hold)", typeof(string)),
        new ToolParameter("timeout",  "Timeout en ms", typeof(string)),
        new ToolParameter("index",    "Numéro d'élément retourné par view (click_index, fill_index)", typeof(string)),
        new ToolParameter("tab",      "Nom de l'onglet/tâche sur lequel agir (ouvre si absente). Permet de gérer plusieurs onglets en parallèle, chacun assigné à une tâche. Ex: \"carbone\", \"aramid\"", typeof(string)),
        new ToolParameter("queries",  "Requêtes distinctes à lancer en PARALLÈLE (parallel_search). Format : séparées par des retours à la ligne ou des ' ;; '. Chaque requête est une 'tâche' indépendante (ex: \"coque carbone s26 ultra\" ;; \"coque aramid s26 ultra\")", typeof(string)),
        new ToolParameter("confirmed", "Doit valoir true uniquement après accord explicite de l'utilisateur pour un clic de paiement/réservation", typeof(string)),
    };

    [ActivatorUtilitiesConstructor]
    public BrowserTool(ILogger<BrowserTool> logger, BrowserManager browserManager, IWebBrowser? webBrowser = null, IComputerController? computer = null)
    {
        _logger         = logger;
        _browserManager = browserManager;
        _webBrowser     = webBrowser;
        _computer       = computer;
    }

    // Constructeur de compatibilité pour les tests (BrowserManager est créé en interne)
    public BrowserTool(ILogger<BrowserTool> logger, IWebBrowser? webBrowser = null)
        : this(logger,
               new BrowserManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserManager>.Instance),
               webBrowser)
    { }

    public async Task<ToolResult> ExecuteAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action",   out var action);
        parameters.TryGetValue("url",      out var url);
        parameters.TryGetValue("query",    out var query);
        parameters.TryGetValue("channel",  out var channel);
        parameters.TryGetValue("selector", out var selector);
        parameters.TryGetValue("text",     out var text);
        // Alias tolérés : le modèle confond souvent text/query.
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(query))
            text = query;
        parameters.TryGetValue("key",      out var key);
        parameters.TryGetValue("x",        out var xStr);
        parameters.TryGetValue("y",        out var yStr);
        parameters.TryGetValue("direction", out var direction);
        parameters.TryGetValue("duration_ms", out var durationStr);
        parameters.TryGetValue("index", out var indexStr);
        parameters.TryGetValue("queries", out var queries);
        parameters.TryGetValue("confirmed", out var confirmedStr);
        int.TryParse(parameters.GetValueOrDefault("timeout"), out var timeout);
        parameters.TryGetValue("tab", out var tab);

        try
        {
            // Onglet nommé : avant toute action, on pointe sur la bonne tâche
            // (ouvre l'onglet nommé s'il n'existe pas encore). Sans conflit entre
            // plusieurs onglets gérés en parallèle. Le nom peut venir du paramètre
            // `tab` OU, en mode multi-agent, du contexte (Metadata["modetab"]).
            var effectiveTab = !string.IsNullOrWhiteSpace(tab)
                ? tab
                : context.Metadata.TryGetValue("modetab", out var mt) && mt is string ms && !string.IsNullOrWhiteSpace(ms)
                    ? ms
                    : null;
            if (!string.IsNullOrWhiteSpace(effectiveTab) && _webBrowser is PlaywrightWebBrowser pw)
            {
                var named = await pw.GetNamedPageAsync(effectiveTab, cancellationToken);
                if (named is null)
                {
                    var t = await pw.NewTabNamedAsync(effectiveTab, null, cancellationToken);
                    if (t is null)
                        return ToolResult.Failed($"Impossible d'ouvrir l'onglet « {effectiveTab} ».");
                }
                _logger.LogInformation("[BrowserTool] Action {Action} ciblée sur l'onglet « {Tab} »", action, effectiveTab);
            }

            // Sécurité (façon tools/navigateur.py) : sur les sites sensibles,
            // lecture autorisée mais AUCUNE action.
            var ecriture = action is "click" or "click_at" or "click_index" or "fill" or "fill_index" or "type";
            if (ecriture && await EstDomaineProtegeAsync())
            {
                _logger.LogWarning("[BrowserTool] Action {Action} refusée : domaine protégé", action);
                return ToolResult.Failed("On est sur un site protégé (banque / impôts / santé). Je peux le lire, mais je n'y fais aucune action : fais-le toi-même.");
            }

            return (action?.ToLowerInvariant()) switch
            {
                "open_url"       => await OpenUrlAsync(url, cancellationToken),
                "navigate"       => await BrowserNavigateAsync(url, cancellationToken),
                "view"           => await BrowserViewAsync(cancellationToken),
                "click_index"    => await BrowserClickIndexAsync(indexStr, confirmedStr, cancellationToken),
                "fill_index"     => await BrowserFillIndexAsync(indexStr, text, cancellationToken),
                "site_search"    => await BrowserSiteSearchAsync(url, text, cancellationToken),
                "parallel_search" => await BrowserParallelSearchAsync(queries, cancellationToken),
                "youtube_latest" => await BrowserYouTubeLatestAsync(channel ?? text ?? query, cancellationToken),
                "youtube_search" => await BrowserYouTubeSearchAsync(text ?? query, cancellationToken),
                "list_tabs"      => await ListTabsAsync(cancellationToken),
                "new_tab"        => await NewTabAsync(url, cancellationToken),
                "focus_tab"      => await FocusTabAsync(indexStr, cancellationToken),
                "close_tab"      => await CloseTabAsync(indexStr, cancellationToken),
                "click"          => await BrowserClickAsync(selector, cancellationToken),
                "click_at"       => await BrowserClickAtAsync(xStr, yStr, cancellationToken),
                "fill"           => await BrowserFillAsync(selector, text, cancellationToken),
                "type"           => await BrowserTypeAsync(text, cancellationToken),
                "press"          => await BrowserPressAsync(key, cancellationToken),
                "hold"           => await BrowserHoldAsync(key, durationStr, cancellationToken),
                "scroll"         => await BrowserScrollAsync(direction, cancellationToken),
                "screenshot"     => await BrowserScreenshotAsync(cancellationToken),
                "get_elements"   => await BrowserGetElementsAsync(selector, cancellationToken),
                "extract"        => await BrowserExtractAsync(cancellationToken),
                "snapshot"       => await BrowserSnapshotAsync(cancellationToken),
                "close_browser"  => await BrowserCloseAsync(cancellationToken),
                "send_keys"      => await SendKeysToChromeAsync(key, cancellationToken),
                "list_windows"   => await ListWindowsAsync(cancellationToken),
                "focus"          => await FocusChromeAsync(cancellationToken),
                _ => ToolResult.Failed($"Action inconnue : '{action}'. Actions valides : open_url, navigate, click, click_at, fill, type, press, hold, scroll, screenshot, get_elements, extract, snapshot, send_keys, list_windows, focus, close_browser")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BrowserTool] Action {Action} échouée", action);
            return ToolResult.Failed($"Erreur navigateur : {ex.Message}");
        }
    }

    // ── open_url ─────────────────────────────────────────────────────────────
    // Essaie d'abord un nouvel onglet CDP (rapide, même fenêtre),
    // sinon BrowserManager (ouvre une nouvelle fenêtre Chrome).
    private async Task<ToolResult> OpenUrlAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Failed("Paramètre 'url' requis.");

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        // Priorité : ouvrir dans un onglet CDP du Chrome existant
        if (_webBrowser is PlaywrightWebBrowser pw)
        {
            try
            {
                var page = await pw.NewTabAsync(url, ct);
                if (page is not null)
                {
                    await page.WaitForTimeoutAsync(800);
                    _logger.LogInformation("[BrowserTool] open_url → onglet CDP : {Url}", url);
                    return ToolResult.Succeeded($"Ouvert dans un onglet : {await page.TitleAsync()}\nURL : {page.Url}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[BrowserTool] CDP indisponible pour open_url, repli BrowserManager");
            }
        }

        // Fallback : BrowserManager (nouvelle fenêtre)
        return _browserManager.OpenUrl(url);
    }

    // ── Playwright actions ───────────────────────────────────────────────────
    private async Task<ToolResult> BrowserNavigateAsync(string? url, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Failed("Paramètre 'url' requis.");

        if (!await _webBrowser.NavigateAsync(url, ct))
        {
            // SON Chrome tourne déjà sans port CDP : on ne peut pas le piloter.
            // On ouvre quand même l'URL dans son Chrome (façon lien QuickShare)
            // plutôt que d'échouer ou de lancer un 2e profil.
            if (_browserManager is not null)
            {
                var openResult = _browserManager.OpenUrl(url);
                if (openResult.Success)
                    return ToolResult.Succeeded($"URL ouverte dans le navigateur utilisateur : {url}");
            }

            return ToolResult.Failed($"Échec de navigation vers : {url}");
        }

        var snapshot  = await _webBrowser.SnapshotAsync(ct);
        var text      = snapshot?.Text ?? string.Empty;
        var truncated = text.Length > 1200 ? text[..1200] + "\n\n[Tronqué]" : text;
        var elements  = await BuildNumberedElementsAsync(ct);
        return ToolResult.Succeeded(
            $"Navigué vers {snapshot?.Url ?? url}.\nTitre : {snapshot?.Title}\n\n{truncated}\n\n{elements}");
    }

    // ── Navigation par INDEX (port du système data-jaridx du repo Python) ────
    private async Task<string> BuildNumberedElementsAsync(CancellationToken ct)
    {
        try
        {
            var page = GetCurrentPage();
            if (page is null) return "(éléments indisponibles)";

            var elements = await page.EvaluateAsync<JsonElement>(JsTagElements);
            if (elements.ValueKind != JsonValueKind.Array)
                return "(aucun élément)";

            var lines = new List<string>();
            foreach (var el in elements.EnumerateArray())
            {
                if (lines.Count >= 80) break;
                var idx = el.TryGetProperty("index", out var iv) ? iv.GetInt32() : -1;
                var tag = el.TryGetProperty("tag", out var t) ? t.GetString() ?? "?" : "?";
                var type = el.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                var label = el.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                var inView = el.TryGetProperty("inView", out var v) && v.GetBoolean();
                lines.Add($"[{idx}] {tag}{(string.IsNullOrEmpty(type) ? "" : "/" + type)}{(inView ? "" : " (hors vue)")} : {label}");
            }
            return lines.Count == 0
                ? "Aucun élément interactif sur la page."
                : $"ÉLÉMENTS CLIQUABLES/SAISISSABLES ({lines.Count}) — clique ou remplis par NUMÉRO :\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BrowserTool] Échec listage éléments numérotés");
            return "(éléments indisponibles)";
        }
    }

    private async Task<ToolResult> BrowserViewAsync(CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        var snapshot = await _webBrowser.SnapshotAsync(ct);
        var text = snapshot?.Text ?? string.Empty;
        var truncated = text.Length > 1000 ? text[..1000] + "\n[Tronqué]" : text;
        var elements = await BuildNumberedElementsAsync(ct);
        return ToolResult.Succeeded($"URL : {snapshot?.Url}\nTitre : {snapshot?.Title}\n\n{truncated}\n\n{elements}");
    }

    // Doctrine reservation.py : jamais de paiement ni de soumission finale sans
    // confirmation humaine explicite (paramètre confirmed=true après accord oral).
    private static readonly string[] RiskySubmitKeywords =
    {
        "payer", "paiement", "payment", "checkout", "commander", "confirmer la commande",
        "réserver", "reserver", "book now", "confirmer la réservation", "valider la commande",
        "finaliser", "acheter", "buy now", "place order", "complete purchase", "confirm booking"
    };

    private async Task<ToolResult> BrowserClickIndexAsync(string? indexStr, string? confirmedStr, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (!int.TryParse(indexStr, out var index) || index < 0)
            return ToolResult.Failed("Paramètre 'index' requis (numéro retourné par view).");

        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        try
        {
            var page = GetCurrentPage();
            if (page is null) return ToolResult.Failed("Page non disponible.");

            var selector = $"[data-jaridx=\"{index}\"]";
            var el = await page.QuerySelectorAsync(selector);
            if (el is null)
                return ToolResult.Failed($"Élément [{index}] introuvable. Fais action=view pour rafraîchir la liste.");

            // Garde-fou réservation/paiement : exige confirmed=true après accord de l'utilisateur.
            var label = (await el.TextContentAsync()) ?? "";
            var elType = (await el.GetAttributeAsync("type"))?.ToLowerInvariant() ?? "";
            var haystack = $"{label} {elType}".ToLowerInvariant();
            var isConfirmed = string.Equals(confirmedStr, "true", StringComparison.OrdinalIgnoreCase);
            if (!isConfirmed && RiskySubmitKeywords.Any(k => haystack.Contains(k)))
                return ToolResult.Failed(
                    $"ACTION SENSIBLE : [{index}] « {label.Trim()} » semble être une soumission finale ou un paiement. " +
                    "Demande une confirmation explicite à l'utilisateur (« Tu confirmes ? »), puis relance ce clic avec confirmed=true UNIQUEMENT après son accord.");

            await page.ClickAsync(selector, new PageClickOptions { Timeout = 8000 });
            await page.WaitForTimeoutAsync(1200);

            var url = page.Url;
            var title = await page.TitleAsync();
            var elements = await BuildNumberedElementsAsync(ct);
            _logger.LogInformation("[BrowserTool] Clic par index [{Index}] → {Url}{Confirmed}", index, url, isConfirmed ? " (confirmé)" : "");
            return ToolResult.Succeeded($"Clic sur [{index}] effectué. Nouvelle page : {title}\nURL : {url}\n\n{elements}");
        }
        catch (Exception ex)
        {
            // La page a pu changer et les index sont périmés : invite à refaire view.
            return ToolResult.Failed($"Échec du clic sur [{index}] (élément introuvable ? Fais action=view pour rafraîchir) : {ex.Message}");
        }
    }

    private async Task<ToolResult> BrowserFillIndexAsync(string? indexStr, string? text, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (!int.TryParse(indexStr, out var index) || index < 0)
            return ToolResult.Failed("Paramètre 'index' requis (numéro retourné par view).");
        if (string.IsNullOrWhiteSpace(text)) return ToolResult.Failed("Paramètre 'text' requis.");

        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        try
        {
            var page = GetCurrentPage();
            if (page is null) return ToolResult.Failed("Page non disponible.");

            var selector = $"[data-jaridx=\"{index}\"]";
            var el = await page.QuerySelectorAsync(selector);
            if (el is null)
                return ToolResult.Failed($"Élément [{index}] introuvable. Fais action=view pour rafraîchir la liste.");
            if ((await el.GetAttributeAsync("type"))?.ToLowerInvariant() == "password")
                return ToolResult.Failed("Champ mot de passe : je ne le remplis jamais, fais-le toi-même.");

            await page.FillAsync(selector, text, new PageFillOptions { Timeout = 8000 });
            _logger.LogInformation("[BrowserTool] Saisie par index [{Index}] : '{Text}'", index, TruncateForLog(text));
            return ToolResult.Succeeded($"'{text}' saisi dans le champ [{index}]. Utilise action=press key=Enter pour valider si besoin.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Échec de saisie dans [{index}] : {ex.Message}");
        }
    }

    private static string TruncateForLog(string s) => s.Length <= 60 ? s : s[..60] + "…";

    // ── Recherche produit déterministe (fast-path, façon repo Python) ────────
    // Trouve la barre de recherche du site par heuristiques CSS génériques,
    // saisit la requête, valide, puis extrait les résultats — SANS round LLM.
    private const string JsFindSearchBox = @"() => {
      const candidates = [
        '#twotabsearchtextbox',
        'input[name=""field-keywords""]',
        'form[role=""search""] input[type=""text""]',
        'form[role=""search""] input[type=""search""]',
        'form[action*=""search""] input[type=""text""]',
        'input[name=""q""]:not([type=""hidden""])',
        'input[name=""k""]:not([type=""hidden""])',
        'input#search',
        'input[type=""search""]',
        'input[placeholder*=""recherch"" i]',
        'input[placeholder*=""search"" i]',
        'input[aria-label*=""recherch"" i]',
        'input[aria-label*=""search"" i]'
      ];
      for (const sel of candidates) {
        const el = document.querySelector(sel);
        if (el && el.offsetParent !== null && !el.disabled) {
          el.setAttribute('data-jaridx', 'jarvis-search');
          return sel;
        }
      }
      return null;
    }";

    private const string JsExtractResults = @"() => {
      const norm = s => (s || '').replace(/\s+/g, ' ').trim();
      const res = [];
      const seen = new Set();
      const push = (title, price, rating, href) => {
        title = norm(title).slice(0, 110);
        if (!title || title.length < 8 || seen.has(title)) return;
        seen.add(title);
        res.push({ title, price: norm(price).slice(0, 30), rating: norm(rating).slice(0, 25), href: href || '' });
      };
      const priceIn = root => {
        const spans = root.querySelectorAll('span[class*=price], span.a-offscreen, span[data-testid=price], p[data-testid=price], div[data-testid=price]');
        for (const sp of spans) { const t = sp.textContent; if (/[0-9]\s?[€$£]/.test(t)) return t; }
        for (const sp of root.querySelectorAll('span')) { const t = sp.textContent; if (/^[0-9]+[,.][0-9]{2}\s?[€$£]/.test(norm(t))) return t; }
        return '';
      };
      const ratingIn = root => {
        const r = root.querySelector('[data-hook=rating-out-of-text], span.a-icon-alt, [aria-label*=étoiles i], [aria-label*=""out of 5 stars"" i]');
        return r ? (r.textContent || r.getAttribute('aria-label') || '') : '';
      };
      let blocks = document.querySelectorAll('div[data-component-type=""s-search-result""], li[data-asin]:not([data-asin=""""]), article[data-testid], div[data-testid=""product-card""]');
      for (const b of blocks) {
        const a = b.querySelector('h2 a, h3 a, a.a-link-normal[href*=dp], a[href]'); 
        if (!a) continue;
        const title = b.querySelector('h2 span, h2, h3')?.textContent || a.getAttribute('aria-label') || a.textContent;
        try { push(title, priceIn(b), ratingIn(b), new URL(a.href, location.origin).pathname.includes('/dp/') ? a.href : ''); } catch {}
        if (res.length >= 10) break;
      }
      if (res.length < 3) {
        res.length = 0; seen.clear();
        for (const a of document.querySelectorAll('main a[href], #content a[href], body a[href]')) {
          const t = a.textContent || '';
          if (t.length < 25 || t.length > 150) continue;
          const href = a.href || '';
          if (!href.startsWith('http') || /\/(gp\/|politics|customer|help|cart)|signin|login|#/.test(href)) continue;
          try { const u = new URL(href); if (u.hostname !== location.hostname && !u.hostname.endsWith(location.hostname.split('.').slice(-2).join('.'))) continue; } catch { continue; }
          const card = a.closest('[data-component-type], li, article, div');
          push(t, card ? priceIn(card) : '', card ? ratingIn(card) : '', '');
          if (res.length >= 10) break;
        }
      }
      return res;
    }";

    private async Task<ToolResult> BrowserSiteSearchAsync(string? url, string? query, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Failed("Paramètre 'text' requis (la requête à chercher sur le site).");

        if (!string.IsNullOrWhiteSpace(url) && !await _webBrowser.NavigateAsync(url, ct))
            return ToolResult.Failed($"Échec de navigation vers : {url}");
        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        var page = GetCurrentPage();
        if (page is null) return ToolResult.Failed("Page non disponible.");

        try
        {
            // 1. Trouver la barre de recherche
            var selector = await page.EvaluateAsync<string>(JsFindSearchBox);
            if (string.IsNullOrEmpty(selector))
                return ToolResult.Failed($"Barre de recherche introuvable sur {page.Url}. Utilise action=view et fill_index à la place.");

            // 2. Saisir + valider
            await page.FillAsync(selector, query, new PageFillOptions { Timeout = 8000 });
            await page.Keyboard.PressAsync("Enter");

            // 3. Attendre les résultats (changement d'URL ou réseau calme)
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 6000 }); }
            catch { /* certains sites ne calment jamais le réseau */ }
            await page.WaitForTimeoutAsync(500);

            // 4. Extraire les résultats
            var results = await page.EvaluateAsync<JsonElement>(JsExtractResults);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Recherche « {query} » effectuée sur {page.Url}.");
            sb.AppendLine($"Titre de la page : {await page.TitleAsync()}");
            sb.AppendLine();
            if (results.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var r in results.EnumerateArray())
                {
                    i++;
                    var title = r.TryGetProperty("title", out var t) ? t.GetString() : "";
                    var price = r.TryGetProperty("price", out var p) ? p.GetString() : "";
                    var rating = r.TryGetProperty("rating", out var ra) ? ra.GetString() : "";
                    var link = r.TryGetProperty("href", out var h) ? h.GetString() : "";
                    sb.Append($"{i}. {title}");
                    if (!string.IsNullOrWhiteSpace(price)) sb.Append($" — {price}");
                    if (!string.IsNullOrWhiteSpace(rating)) sb.Append($" ({rating})");
                    if (!string.IsNullOrWhiteSpace(link)) sb.Append($"\n   Lien: {link}");
                    sb.AppendLine();
                }
                if (i == 0)
                {
                    var text = await _webBrowser.GetTextAsync(ct);
                    text = text.Length > 1500 ? text[..1500] : text;
                    sb.AppendLine("(Aucun résultat structuré extrait — extrait du texte de la page :)").AppendLine(text);
                }
            }

            // 5. Retaguer les éléments pour un éventuel click_index ultérieur
            var elements = await BuildNumberedElementsAsync(ct);
            sb.AppendLine().AppendLine(elements);
            _logger.LogInformation("[BrowserTool] site_search '{Query}' → page {Url}", TruncateForLog(query), page.Url);
            return ToolResult.Succeeded(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"site_search échoué ({ex.Message}). Utilise action=view puis fill_index.");
        }
    }

    // ── parallel_search ──────────────────────────────────────────────────────
    // Lance PLUSIEURS recherches indépendantes en parallèle (chacune = une
    // « tâche »), puis agrège les résultats. Très rapide : elles s'exécutent
    // vraiment en même temps via Task.WhenAll sur des requêtes HTTP distinctes —
    // aucun verrou navigateur, aucune dépendance entre les tâches.
    private async Task<ToolResult> BrowserParallelSearchAsync(string? queries, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queries))
            return ToolResult.Failed("Paramètre 'queries' requis : plusieurs requêtes séparées par ' ;; ' ou des retours à la ligne.");

        // Découpe en tâches indépendantes (séparateurs : ';;', ';', nouvelles lignes).
        var tasks = queries
            .Split(new[] { ";;", ";\n", "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(q => q.Trim().Trim(';', ' ', '\t', '\r', '\n'))
            .Where(q => q.Length >= 2)
            .ToList();
        if (tasks.Count == 0)
            return ToolResult.Failed("Aucune requête valide dans 'queries'.");
        if (tasks.Count > 6)
        {
            _logger.LogWarning("[BrowserTool] parallel_search plafonné à 6 tâches (reçu {N})", tasks.Count);
            tasks = tasks.Take(6).ToList();
        }

        // Chaque tâche part en parallèle, sans attendre les autres.
        var results = await Task.WhenAll(tasks.Select(q => RunSearchTaskAsync(q, ct)));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Recherche parallèle de {tasks.Count} requêtes (done en parallèle) :");
        for (var i = 0; i < results.Length; i++)
        {
            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"TÂCHE {i + 1} : « {tasks[i]} »");
            sb.AppendLine(results[i]);
        }
        return ToolResult.Succeeded(sb.ToString().TrimEnd());
    }

    private async Task<string> RunSearchTaskAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query);
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return $"(Échec HTTP {resp.StatusCode})";
            var html = await resp.Content.ReadAsStringAsync(ct);

            // Extraction des liens de résultats (DuckDuckGo Lite : <a rel="nofollow" class="result-link" href="...">Titre</a>)
            var results = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(html, "<a[^>]*class=\"result-link\"[^>]*href=\"([^\"]+)\"[^>]*>([^<]*)</a>"))
            {
                var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                var title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;
                results.Add($"{title}\n   {href}");
                if (results.Count >= 8) break;
            }
            if (results.Count == 0)
            {
                // Repli : grossier mais utile — tous les liens externes.
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(html, "<a[^>]*href=\"(http[^\"]+)\"[^>]*>([^<]*)</a>"))
                {
                    var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                    var title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    results.Add($"{title}\n   {href}");
                    if (results.Count >= 8) break;
                }
            }
            return results.Count == 0
                ? "(Aucun résultat extrait — la page de recherche n'a rien retourné)"
                : string.Join("\n", results);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "(annulé)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BrowserTool] Tâche de recherche échouée: {Q}", TruncateForLog(query));
            return $"(Erreur : {ex.Message})";
        }
    }

    // ── youtube_latest ───────────────────────────────────────────────────────
    // « Ouvre la dernière vidéo de X » : une action, zéro round LLM.
    // 1. Page /@handle/videos (slug dérivé du nom) ; 2. extraction du 1er
    // /watch?v= ; 3. fallback recherche triée par date ; 4. ouverture vidéo.
    public static string ToYouTubeHandleSlug(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return "";
        var normalized = channel.Trim().TrimStart('@');
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
            else if (c == '-' || c == '_' || c == '.')
                sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task<ToolResult> BrowserYouTubeLatestAsync(string? channel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(channel))
            return ToolResult.Failed("Paramètre 'channel' requis (nom de la chaîne YouTube).");

        var name = channel.Trim().TrimStart('@');

        // Chemin PRIMAIRE et fiable : yt-dlp résout la chaîne PAR SON NOM (l'utilisateur
        // donne le nom, pas un @identifiant) et récupère sa TOUTE dernière vidéo. On
        // ouvre ensuite la vidéo dans le VRAI navigateur de l'utilisateur. Ne dépend
        // d'aucun contrôle CDP ni d'un 2e profil.
        if (!string.IsNullOrWhiteSpace(name))
        {
            var resolved = await ResolveLatestViaYtDlpAsync(name, ct);
            if (resolved is not null && _browserManager is not null)
            {
                var opened = _browserManager.OpenUrl(resolved.Value.Url);
                if (opened.Success)
                    return ToolResult.Succeeded($"Vidéo lancée — « {resolved.Value.Title} » : {resolved.Value.Url}");
            }
        }

        // Repli navigateur : uniquement utile si le navigateur est réellement
        // pilotable (CDP actif). Dans le cas courant (Chrome utilisateur déjà
        // ouvert sans port CDP) il échoue proprement, sans lancer de 2e profil.
        var slug = ToYouTubeHandleSlug(name);

        // Ordre des candidats (le plus fiable d'abord) :
        // 1. Recherche de la CHAÎNE par son NOM (l'utilisateur dit le nom, pas un
        //    @identifiant) → on récupère la vraie page /@handle/videos de la chaîne.
        // 2. À défaut, on devine /@{slug}/videos (repli).
        // 3. Recherche triée par date du NOM (tout dernier recours, car elle peut
        //    remonter des vidéos d'autres chaînes homonymes).
        var candidates = new List<(string Url, bool IsChannelSearch)>
        {
            ($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(name)}&sp=EgIQAg%253D%253D", true)
        };
        if (!string.IsNullOrEmpty(slug))
            candidates.Add(($"https://www.youtube.com/@{Uri.EscapeDataString(slug)}/videos", false));
        candidates.Add(($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(name)}&sp=CAI%253D%253D", false));

        try
        {
            if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
            if (!await _webBrowser.LaunchAsync(ct))
            {
                // Le navigateur CDP n'est pas disponible (Chrome ouvert sans port
                // de débogage). On ouvre la recherche YouTube dans le vrai
                // navigateur de l'utilisateur au lieu de retourner une erreur.
                var fallbackUrl = candidates[0].Url;
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = fallbackUrl, UseShellExecute = true });
                }
                catch { }
                return ToolResult.Succeeded($"Recherche YouTube ouverte dans le navigateur : {fallbackUrl}");
            }
            var page = GetCurrentPage();
            if (page is null) return ToolResult.Failed("Page non disponible.");

            string? videoUrl = null;
            string? videoTitle = null;
            string triedPages = "";

            foreach (var (pageUrl, isChannelSearch) in candidates)
            {
                var nav = await _webBrowser.NavigateAsync(pageUrl, ct);
                if (!nav)
                {
                    triedPages += $" {pageUrl} (échec navigation);";
                    continue;
                }

                // La recherche de chaîne : on rebondit vers la VRAIE chaîne
                // (retrouvée par son nom), puis on regarde ses vidéos.
                string? target = pageUrl;
                if (isChannelSearch)
                {
                    try
                    {
                        var resolved = await ResolveChannelVideosUrlAsync(page);
                        if (!string.IsNullOrWhiteSpace(resolved))
                            target = resolved;
                    }
                    catch { /* on reste sur la page de recherche */ }
                }

                if (target != pageUrl)
                    await _webBrowser.NavigateAsync(target, ct);

                // Le rendu JS de YouTube prend 1-3 s : on sonde jusqu'à 8 s.
                for (var attempt = 0; attempt < 16 && videoUrl is null; attempt++)
                {
                    await page.WaitForTimeoutAsync(500);
                    try
                    {
                        var links = await GetWatchLinksAsync(page);
                        if (links.Count > 0)
                        {
                            videoUrl = links[0].Url;
                            videoTitle = links[0].Title;
                        }
                    }
                    catch { /* DOM pas prêt, on retente */ }
                }

                if (videoUrl is not null) break;
                triedPages += $" {pageUrl} (aucune vidéo trouvée);";
            }

            if (videoUrl is null)
                return ToolResult.Failed($"Impossible de trouver la dernière vidéo de « {channel} ». Pages essayées :{triedPages}");

            // On lance la vidéo dans un vrai onglet visible
            var opened = _browserManager.OpenUrl(videoUrl);
            var titlePart = string.IsNullOrWhiteSpace(videoTitle) ? "" : $" — « {videoTitle} »";
            return opened.Success
                ? ToolResult.Succeeded($"Vidéo lancée{titlePart} : {videoUrl}")
                : ToolResult.Succeeded($"Dernière vidéo trouvée{titlePart} : {videoUrl} (ouverture directe bloquée par la limite d'onglets)");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"youtube_latest échoué ({ex.Message}).");
        }
    }

    // ── youtube_search ──────────────────────────────────────────────────────
    // Recherche une vidéo spécifique sur YouTube via yt-dlp (ytsearch1:) et
    // ouvre le résultat dans le vrai navigateur. Utile quand on cherche une
    // vidéo précise (ex: « short mrbeast danse meme ») au lieu de la dernière
    // vidéo d'une chaîne.
    private async Task<ToolResult> BrowserYouTubeSearchAsync(string? searchText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return ToolResult.Failed("Paramètre 'text' ou 'query' requis (ex: 'mrbeast danse meme short').");

        var query = searchText.Trim();

        // 1) Utiliser yt-dlp pour résoudre la première vidéo correspondant à la recherche
        var ytdlp = FindYtDlp();
        if (ytdlp is not null)
        {
            try
            {
                var lines = await RunYtdlpLinesAsync(ytdlp,
                    $"--skip-download --print \"%(id)s|||%(title)s\" \"ytsearch1:{query}\"", ct);
                var first = lines.FirstOrDefault(l => l.Contains("|||"));
                if (first is not null)
                {
                    var parts = first.Split("|||", 2);
                    var videoId = parts[0].Trim();
                    var title = parts.Length > 1 ? parts[1].Trim() : "";
                    if (!string.IsNullOrEmpty(videoId) && videoId.StartsWith("http"))
                    {
                        // yt-dlp a renvoyé une URL directe
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = videoId, UseShellExecute = true });
                        }
                        catch { }
                        return ToolResult.Succeeded($"Vidéo trouvée — « {title} » : {videoId}");
                    }
                    else if (!string.IsNullOrEmpty(videoId))
                    {
                        // yt-dlp a renvoyé un ID → construire l'URL
                        var url = $"https://www.youtube.com/watch?v={videoId}";
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                        }
                        catch { }
                        return ToolResult.Succeeded($"Vidéo trouvée — « {title} » : {url}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[BrowserTool] youtube_search yt-dlp échoué, repli sur ouverture directe");
            }
        }

        // 2) Fallback : ouvrir la recherche YouTube dans le vrai navigateur
        var searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = searchUrl, UseShellExecute = true });
        }
        catch { }
        return ToolResult.Succeeded($"Recherche YouTube ouverte dans le navigateur : {searchUrl}");
    }

    /// <summary>Localise yt-dlp (PATH + WinGet). Renvoie le chemin exe ou null.</summary>
    private static string? FindYtDlp()
    {
        foreach (var name in new[] { "yt-dlp.exe", "yt-dlp" })
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        try
        {
            var packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                var hit = Directory.GetFiles(packages, "yt-dlp.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Résout la TOUTE dernière vidéo d'une chaîne YouTube PAR SON NOM
    /// (pas un @identifiant deviné) grâce à yt-dlp :
    /// 1. recherche « Chaînes » → première chaîne dont le nom correspond ;
    /// 2. ongolet /videos de cette chaîne → première (dernière) vidéo.
    /// Renvoie (Url, Titre) ou null si introuvable.</summary>
    private static async Task<(string Url, string Title)?> ResolveLatestViaYtDlpAsync(string name, CancellationToken ct)
    {
        var ytdlp = FindYtDlp();
        if (ytdlp is null) return null;

        try
        {
            // 1) Trouver l'ID de la VRAIE chaîne par son nom (filtre « Chaînes »).
            var searchUrl = "https://www.youtube.com/results?search_query=" +
                            Uri.EscapeDataString(name) + "&sp=EgIQAg%3D%3D";
            var channels = await RunYtdlpLinesAsync(ytdlp,
                $"--skip-download --flat-playlist --playlist-end 12 --print \"%(uploader)s|%(id)s\" \"{searchUrl}\"", ct);
            string? channelId = null;
            foreach (var line in channels)
            {
                var idx = line.IndexOf('|');
                if (idx <= 0) continue;
                var uploader = line[..idx].Trim();
                var id = line[(idx + 1)..].Trim();
                if (!id.StartsWith("UC", StringComparison.Ordinal)) continue;
                if (NamesMatch(name, uploader)) { channelId = id; break; }
            }
            if (channelId is null) return null;

            // 2) Première vidéo de cette chaîne = sa dernière publication.
            var videosUrl = "https://www.youtube.com/channel/" + channelId + "/videos";
            var first = await RunYtdlpLinesAsync(ytdlp,
                $"--skip-download --flat-playlist --playlist-end 1 --print \"%(title)s|%(id)s\" \"{videosUrl}\"", ct);
            foreach (var line in first)
            {
                var idx = line.IndexOf('|');
                if (idx <= 0) continue;
                var title = line[..idx].Trim();
                var id = line[(idx + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(id)) continue;
                return (("https://www.youtube.com/watch?v=" + id), string.IsNullOrWhiteSpace(title) ? "dernière vidéo" : title);
            }
        }
        catch { }
        return null;
    }

    private static bool NamesMatch(string wanted, string actual)
    {
        string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD))
            {
                if (char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark &&
                    (char.IsLetterOrDigit(c) || c == ' ' || c == '-'))
                    sb.Append(c);
            }
            return sb.ToString().Trim();
        }
        var w = Norm(wanted);
        var a = Norm(actual);
        if (string.IsNullOrEmpty(w)) return false;
        return a.Contains(w, StringComparison.Ordinal) || w.Contains(a, StringComparison.Ordinal);
    }

    private static async Task<List<string>> RunYtdlpLinesAsync(string ytdlp, string arguments, CancellationToken ct)
    {
        var lines = new List<string>();
        using var p = new System.Diagnostics.Process();
        p.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ytdlp,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        try
        {
            p.Start();
            var read = p.StandardOutput.ReadToEndAsync();
            var done = await Task.WhenAny(read, Task.Delay(20000, ct));
            if (done != read) return lines;
            foreach (var l in (await read).Split('\n'))
            {
                var t = l.Trim();
                if (t.Length > 0) lines.Add(t);
            }
        }
        catch { }
        return lines;
    }

    /// <summary>Sur une page de résultats YouTube, renvoie l'URL '/@handle/videos'
    /// de la PREMIÈRE chaîne correspondante (l'utilisateur a donné le NOM de la
    /// chaîne, pas son identifiant). Renvoie null si aucune chaîne trouvée.</summary>
    private static async Task<string?> ResolveChannelVideosUrlAsync(IPage page)
    {
        var js = @"() => {
            const links = Array.from(document.querySelectorAll('a[href*=""/@""], a[href*=""/channel/""]'));
            for (const a of links) {
                const h = a.getAttribute('href') || '';
                if (h.startsWith('/@') || h.startsWith('/channel/')) {
                    return h.split('?')[0].replace(/\/$/, '') + '/videos';
                }
            }
            return null;
        }";
        try
        {
            var result = await page.EvaluateAsync<System.Text.Json.JsonElement>(js);
            return result.ValueKind == System.Text.Json.JsonValueKind.String
                ? result.GetString()
                : null;
        }
        catch { return null; }
    }

    private async Task<List<(string Url, string Title)>> GetWatchLinksAsync(IPage page)
    {
        var js = @"() => {
            const seen = new Set();
            const out = [];
            const anchors = document.querySelectorAll('a#video-title-link, a#video-title');
            for (const a of anchors) {
                const href = (a.getAttribute('href') || '').split('?')[0];
                if (!href.startsWith('/watch?v=') || href.includes('/shorts')) continue;
                const txt = (a.getAttribute('title') || a.textContent || '').trim();
                if (!txt || /^(regarder|watch|à regarder plus tard|play|ignorer)$/i.test(txt)) continue;
                if (seen.has(href)) continue;
                seen.add(href);
                out.push({ href, title: txt.slice(0, 140) });
                if (out.length >= 5) break;
            }
            return out;
        }";
        var raw = await page.EvaluateAsync<System.Text.Json.JsonElement>(js);
        var list = new List<(string, string)>();
        if (raw.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in raw.EnumerateArray())
            {
                var href = item.TryGetProperty("href", out var h) ? h.GetString() : null;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                if (string.IsNullOrWhiteSpace(href)) continue;
                list.Add(("https://www.youtube.com" + href, title ?? ""));
            }
        }
        return list;
    }

    private async Task<ToolResult> BrowserClickAsync(string? selector, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (string.IsNullOrWhiteSpace(selector)) return ToolResult.Failed("Paramètre 'selector' requis.");
        return await _webBrowser.ClickAsync(selector, ct)
            ? ToolResult.Succeeded($"Clic sur : {selector}")
            : ToolResult.Failed($"Échec du clic sur : {selector}");
    }

    private async Task<ToolResult> BrowserFillAsync(string? selector, string? text, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (string.IsNullOrWhiteSpace(selector)) return ToolResult.Failed("Paramètre 'selector' requis.");
        if (text is null) return ToolResult.Failed("Paramètre 'text' requis.");
        return await _webBrowser.FillAsync(selector, text, ct)
            ? ToolResult.Succeeded($"Rempli '{text}' dans {selector}")
            : ToolResult.Failed($"Échec du remplissage dans : {selector}");
    }

    private async Task<ToolResult> BrowserTypeAsync(string? text, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (string.IsNullOrWhiteSpace(text)) return ToolResult.Failed("Paramètre 'text' requis.");
        return await _webBrowser.TypeAsync(text, ct)
            ? ToolResult.Succeeded("Texte saisi.")
            : ToolResult.Failed("Échec de la saisie.");
    }

    private async Task<ToolResult> BrowserPressAsync(string? key, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (string.IsNullOrWhiteSpace(key)) return ToolResult.Failed("Paramètre 'key' requis.");
        return await _webBrowser.PressAsync(key, ct)
            ? ToolResult.Succeeded($"Touche pressée : {key}")
            : ToolResult.Failed($"Échec pour la touche : {key}");
    }

    private async Task<ToolResult> BrowserExtractAsync(CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        var snapshot = await _webBrowser.SnapshotAsync(ct);
        if (snapshot is null) return ToolResult.Failed("Impossible de lire la page courante.");
        var truncated = snapshot.Text.Length > 10000
            ? snapshot.Text[..10000] + "\n\n[Tronqué]"
            : snapshot.Text;
        return ToolResult.Succeeded($"URL : {snapshot.Url}\nTitre : {snapshot.Title}\n\n{truncated}");
    }

    private async Task<ToolResult> BrowserSnapshotAsync(CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        var snapshot = await _webBrowser.SnapshotAsync(ct);
        if (snapshot is null) return ToolResult.Failed("Impossible de lire la page courante.");
        var summary = new
        {
            url             = snapshot.Url,
            title           = snapshot.Title,
            text_length     = snapshot.Text.Length,
            has_screenshot  = snapshot.ScreenshotBase64 is not null,
            text_preview    = snapshot.Text.Length > 2000 ? snapshot.Text[..2000] + "..." : snapshot.Text
        };
        return ToolResult.Succeeded(JsonSerializer.Serialize(summary));
    }

    private async Task<ToolResult> BrowserCloseAsync(CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        return await _webBrowser.CloseAsync(ct)
            ? ToolResult.Succeeded("Navigateur fermé.")
            : ToolResult.Failed("Échec de fermeture du navigateur.");
    }

    // ── Full page control (click_at, hold, scroll, screenshot, get_elements) ─
    private async Task<ToolResult> BrowserClickAtAsync(string? xStr, string? yStr, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (!double.TryParse(xStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(yStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
            return ToolResult.Failed("Paramètres 'x' et 'y' requis (nombres).");

        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        try
        {
            var page = GetCurrentPage();
            if (page is null) return ToolResult.Failed("Page non disponible.");

            await page.Mouse.ClickAsync((float)x, (float)y);
            _logger.LogInformation("[BrowserTool] Clic à ({X}, {Y})", x, y);
            return ToolResult.Succeeded($"Clic effectué à ({x}, {y}).");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur clic ({x},{y}) : {ex.Message}");
        }
    }

    private async Task<ToolResult> BrowserHoldAsync(string? key, string? durationStr, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (string.IsNullOrWhiteSpace(key)) return ToolResult.Failed("Paramètre 'key' requis.");

        int.TryParse(durationStr, out var durationMs);
        if (durationMs <= 0) durationMs = 1000;

        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        try
        {
            var page = GetCurrentPage();
            if (page is null) return ToolResult.Failed("Page non disponible.");

            await page.Keyboard.PressAsync(key);
            await Task.Delay(durationMs, ct);
            return ToolResult.Succeeded($"Touche '{key}' maintenue {durationMs}ms.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur hold '{key}' : {ex.Message}");
        }
    }

    private async Task<ToolResult> BrowserScrollAsync(string? direction, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");

        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        try
        {
            var page = GetCurrentPage();
            if (page is null) return ToolResult.Failed("Page non disponible.");

            var scrollY = (direction?.ToLowerInvariant()) switch
            {
                "up" => -500,
                "down" => 500,
                "page_up" => -1000,
                "page_down" or null => 1000,
                _ => 500
            };

            await page.EvaluateAsync($"() => window.scrollBy(0, {scrollY})");
            _logger.LogInformation("[BrowserTool] Scroll {Direction} ({Pixels}px)", direction ?? "down", scrollY);
            return ToolResult.Succeeded($"Défilement {direction ?? "bas"} ({scrollY}px).");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur scroll : {ex.Message}");
        }
    }

    private async Task<ToolResult> BrowserScreenshotAsync(CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");

        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        try
        {
            var snapshot = await _webBrowser.SnapshotAsync(ct);
            if (snapshot is null)
                return ToolResult.Failed("Impossible de capturer la page.");

            var info = $"URL : {snapshot.Url}\nTitre : {snapshot.Title}\n\nTexte :\n{snapshot.Text}";
            if (snapshot.Text.Length > 8000)
                info = info[..8000] + "\n\n[Tronqué]";
            return ToolResult.Succeeded(info);
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur screenshot : {ex.Message}");
        }
    }

    private async Task<ToolResult> BrowserGetElementsAsync(string? selector, CancellationToken ct)
    {
        if (_webBrowser is null) return ToolResult.Failed("Navigateur autonome indisponible.");

        if (!await _webBrowser.LaunchAsync(ct))
            return ToolResult.Failed("Impossible de lancer le navigateur.");

        try
        {
            var page = GetCurrentPage();
            if (page is null) return ToolResult.Failed("Page non disponible.");

            var cssSelector = string.IsNullOrWhiteSpace(selector)
                ? "a, button, input, textarea, select, [role='button'], [role='link'], [onclick]"
                : selector;

            var elements = await page.EvaluateAsync<JsonElement>(@"
                (selector) => {
                    const els = document.querySelectorAll(selector);
                    return Array.from(els).slice(0, 50).map(el => {
                        const rect = el.getBoundingClientRect();
                        return {
                            tag: el.tagName.toLowerCase(),
                            text: (el.textContent || '').trim().substring(0, 100),
                            href: el.href || '',
                            id: el.id || '',
                            className: el.className || '',
                            x: Math.round(rect.x + rect.width/2),
                            y: Math.round(rect.y + rect.height/2),
                            visible: rect.width > 0 && rect.height > 0
                        };
                    }).filter(e => e.visible);
                }", cssSelector);

            var lines = new List<string>();
            if (elements.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var el in elements.EnumerateArray())
                {
                    var tag = el.TryGetProperty("tag", out var t) ? t.GetString() : "?";
                    var txt = el.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                    var x = el.TryGetProperty("x", out var xv) ? xv.GetInt32() : 0;
                    var y = el.TryGetProperty("y", out var yv) ? yv.GetInt32() : 0;
                    var id = el.TryGetProperty("id", out var idv) ? idv.GetString() : "";
                    var href = el.TryGetProperty("href", out var hv) ? hv.GetString() : "";

                    var desc = $"[{i}] <{tag}> \"{txt}\"";
                    if (!string.IsNullOrWhiteSpace(id)) desc += $" id={id}";
                    if (!string.IsNullOrWhiteSpace(href)) desc += $" href={href}";
                    desc += $" @ ({x},{y})";
                    lines.Add(desc);
                    i++;
                }
            }

            if (lines.Count == 0)
                return ToolResult.Succeeded("Aucun élément interactif trouvé.");

            return ToolResult.Succeeded($"Éléments interactifs ({lines.Count}) :\n{string.Join("\n", lines)}");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"Erreur get_elements : {ex.Message}");
        }
    }

    private IPage? GetCurrentPage()
    {
        if (_webBrowser is PlaywrightWebBrowser pw)
            return pw.GetPage();
        return null;
    }

    private static readonly string[] DomainesProteges =
    {
        "banque", "bnpparibas", "societegenerale", "creditagricole", "boursorama",
        "caisse-epargne", "creditmutuel", "lcl.fr", "ing.fr", "monabanq", "hellobank",
        "impots.gouv", "ameli.fr", "caf.fr", "ants.gouv", "service-public", "laposte.fr"
    };

    private static bool EstUrlPlaceholder(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("example.com") || lower.Contains("example.org") || lower.Contains("example.net")) return true;
        if (lower.Contains("twitter.com") || lower.Contains("t.co")) return true;
        // x.com = nouveau domaine Twitter, bloque seulement si c'est bien le domaine (pas un sous-chemin)
        try
        {
            var host = new Uri(lower.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? lower : "https://" + lower).Host;
            if (host == "x.com" || host.EndsWith(".x.com", StringComparison.Ordinal)) return true;
        }
        catch { }
        if (lower.Contains("placeholder")) return true;
        return false;
    }

    private static bool CommandTextMentionsDomain(string commandText, string url)
    {
        if (string.IsNullOrWhiteSpace(commandText) || string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var host = new Uri(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url).Host.ToLowerInvariant();
            return commandText.ToLowerInvariant().Contains(host);
        }
        catch { return commandText.ToLowerInvariant().Contains(url.ToLowerInvariant()); }
    }

    /// <summary>L'URL courante du navigateur appartient-elle à un domaine
    /// sensible (banque, administration, santé) ?</summary>
    private async Task<bool> EstDomaineProtegeAsync()
    {
        try
        {
            // Async pur : un GetAwaiter().GetResult() synchrones sur le thread du
            // circuit Blazor (connecté au signal de base) DEADLOCK (même famille que
            // l'OCR) → tout gèle. On await vraiment, sans bloquer le circuit.
            if (_webBrowser is null) return false;
            var url = await _webBrowser.GetUrlAsync();
            if (string.IsNullOrWhiteSpace(url)) return false;
            var host = new Uri(url).Host.ToLowerInvariant();
            return DomainesProteges.Any(d => host.Contains(d, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    // ── Onglets (ton vrai Chrome, profil persistant) ─────────────────────────
    private async Task<ToolResult> ListTabsAsync(CancellationToken ct)
    {
        if (_webBrowser is not PlaywrightWebBrowser pw) return ToolResult.Failed("Navigateur autonome indisponible.");
        var pages = pw.GetPages();
        if (pages.Count == 0) return ToolResult.Failed("Aucun onglet ouvert.");
        var sb = new System.Text.StringBuilder($"ONGLETS ({pages.Count}) — l'astérisque * marque l'onglet actif :\n");
        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            string title;
            try { title = await p.TitleAsync(); } catch { title = "?"; }
            sb.AppendLine($"  [{i}]{(ReferenceEquals(p, pw.GetPage()) ? "*" : "")} {title}\n      {p.Url}");
        }
        return ToolResult.Succeeded(sb.ToString());
    }

    private async Task<ToolResult> NewTabAsync(string? url, CancellationToken ct)
    {
        if (_webBrowser is not PlaywrightWebBrowser pw) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Failed("Paramètre 'url' requis.");
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        var page = await pw.NewTabAsync(url, ct);
        if (page is null) return ToolResult.Failed("Impossible d'ouvrir un nouvel onglet.");
        await page.WaitForTimeoutAsync(1200);
        var elements = await BuildNumberedElementsAsync(ct);
        return ToolResult.Succeeded($"Nouvel onglet ouvert : {(await page.TitleAsync())}\nURL : {page.Url}\n\n{elements}");
    }

    private async Task<ToolResult> FocusTabAsync(string? indexStr, CancellationToken ct)
    {
        if (_webBrowser is not PlaywrightWebBrowser pw) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (!int.TryParse(indexStr, out var idx)) return ToolResult.Failed("Paramètre 'index' requis (numéro retourné par list_tabs).");
        if (!await pw.FocusTabAsync(idx, ct)) return ToolResult.Failed($"Onglet [{idx}] introuvable. Fais action=list_tabs.");
        var page = pw.GetPage()!;
        await page.WaitForTimeoutAsync(600);
        var elements = await BuildNumberedElementsAsync(ct);
        return ToolResult.Succeeded($"Onglet actif : {(await page.TitleAsync())}\nURL : {page.Url}\n\n{elements}");
    }

    private async Task<ToolResult> CloseTabAsync(string? indexStr, CancellationToken ct)
    {
        if (_webBrowser is not PlaywrightWebBrowser pw) return ToolResult.Failed("Navigateur autonome indisponible.");
        if (!int.TryParse(indexStr, out var idx)) return ToolResult.Failed("Paramètre 'index' requis.");
        var ok = await pw.CloseTabAsync(idx, ct);
        return ok
            ? ToolResult.Succeeded($"Onglet [{idx}] fermé. ACTION TERMINÉE.")
            : ToolResult.Failed($"Onglet [{idx}] introuvable.");
    }

    // ── Chrome control (send_keys, list_windows, focus) ─────────────────────
    private async Task<ToolResult> SendKeysToChromeAsync(string? key, CancellationToken ct)
    {
        if (_computer is null) return ToolResult.Failed("Contrôle d'ordinateur indisponible.");
        if (string.IsNullOrWhiteSpace(key)) return ToolResult.Failed("Paramètre 'key' requis.");

        var chrome = await FindChromeWindowAsync(ct);
        if (chrome is null)
            return ToolResult.Failed("Aucune fenêtre Chrome trouvée. Ouvrez Chrome d'abord.");

        if (!chrome.IsFocused)
            await _computer.FocusWindowAsync(chrome.Handle, ct);

        var sent = await _computer.PressKeyAsync(key, ct);
        return sent
            ? ToolResult.Succeeded($"ACTION TERMINÉE — Touche '{key}' envoyée à Chrome ({chrome.Title}).")
            : ToolResult.Failed($"Échec d'envoi de la touche '{key}' à Chrome.");
    }

    private async Task<ToolResult> ListWindowsAsync(CancellationToken ct)
    {
        if (_computer is null) return ToolResult.Failed("Contrôle d'ordinateur indisponible.");

        var windows = await _computer.ListWindowsAsync(ct);
        if (windows.Count == 0)
            return ToolResult.Succeeded("Aucune fenêtre visible.");

        var lines = windows.Select(w =>
            $"{(w.IsFocused ? "▶ " : "  ")}[{w.Handle}] {w.Title} ({w.Width}x{w.Height})");
        return ToolResult.Succeeded($"Fenêtres visibles ({windows.Count}) :\n{string.Join("\n", lines)}");
    }

    private async Task<ToolResult> FocusChromeAsync(CancellationToken ct)
    {
        if (_computer is null) return ToolResult.Failed("Contrôle d'ordinateur indisponible.");

        var chrome = await FindChromeWindowAsync(ct);
        if (chrome is null)
            return ToolResult.Failed("Aucune fenêtre Chrome trouvée.");

        if (chrome.IsFocused)
            return ToolResult.Succeeded($"Chrome est déjà au premier plan : {chrome.Title}");

        var focused = await _computer.FocusWindowAsync(chrome.Handle, ct);
        return focused
            ? ToolResult.Succeeded($"ACTION TERMINÉE — Chrome mis au premier plan : {chrome.Title}")
            : ToolResult.Failed($"Échec de mise au premier plan de Chrome.");
    }

    private async Task<WindowInfo?> FindChromeWindowAsync(CancellationToken ct)
    {
        if (_computer is null) return null;
        var windows = await _computer.ListWindowsAsync(ct);
        return windows.FirstOrDefault(w =>
            w.Title.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
            w.Title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
            w.Title.Contains("Chromium", StringComparison.OrdinalIgnoreCase));
    }
}
