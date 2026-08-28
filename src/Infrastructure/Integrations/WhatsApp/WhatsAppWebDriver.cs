using JarvisAI.Application.Services;
using JarvisAI.Application.WebAutomation;
using JarvisAI.Infrastructure.WebAutomation;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace JarvisAI.Infrastructure.Integrations.WhatsApp;

/// <summary>
/// Pilote WhatsApp Web via Playwright en réutilisant l'instance navigateur
/// persistante de Jarvis (profil Chrome dédié : la connexion QR WhatsApp est
/// conservée entre les redémarrages).
/// C'est une automation UI (WhatsApp Web n'expose aucune API publique), donc
/// les sélecteurs sont défensifs (multi-fallbacks) et les opérations best-effort.
/// </summary>
public sealed class WhatsAppWebDriver
{
    private const string TabName = "whatsapp";

    private readonly IWebBrowser _webBrowser;
    private readonly ILogger<WhatsAppWebDriver> _logger;

    public WhatsAppWebDriver(IWebBrowser webBrowser, ILogger<WhatsAppWebDriver> logger)
    {
        _webBrowser = webBrowser;
        _logger = logger;
    }

    public bool IsAvailable => true;

    private PlaywrightWebBrowser? Pw => _webBrowser as PlaywrightWebBrowser;

    private async Task<IPage> GetOrOpenPageAsync(CancellationToken ct)
    {
        // Onglet nommé dédié WhatsApp : on rouvre le même onglet s'il existe déjà.
        var pw = Pw;
        if (pw is not null)
        {
            var existing = await pw.GetNamedPageAsync(TabName, ct);
            if (existing is not null && !existing.IsClosed && existing.Url.Contains("whatsapp"))
                return existing;
        }

        var page = pw is not null
            ? await pw.NewTabNamedAsync(TabName, "https://web.whatsapp.com/", ct)
            : await _webBrowser.LaunchAsync(ct) is false ? throw new InvalidOperationException("Navigateur indisponible") : pw!.GetPage();

        if (page is null)
            throw new InvalidOperationException("Impossible d'ouvrir WhatsApp Web (navigateur indisponible).");
        return page;
    }

    private async Task<bool> WaitForElementAsync(IPage page, string[] selectors, int timeoutMs, CancellationToken ct)
    {
        foreach (var sel in selectors)
        {
            try
            {
                var el = page.Locator(sel).First;
                if (await el.CountAsync() > 0 && await el.IsVisibleAsync())
                    return true;
            }
            catch { }
        }
        // Retry polling (WhatsApp fait des rendus asynchrones)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            foreach (var sel in selectors)
            {
                try
                {
                    var el = page.Locator(sel).First;
                    if (await el.CountAsync() > 0 && await el.IsVisibleAsync())
                        return true;
                }
                catch { }
            }
            try { await page.WaitForTimeoutAsync(400); } catch { }
        }
        _logger.LogWarning("[WhatsApp] Aucun sélecteur visible parmi: {Selectors}", string.Join(" | ", selectors));
        return false;
    }

    private async Task<bool> IsLoggedIn(IPage page)
    {
        try
        {
            var search = await page.Locator("[data-testid=\"chat-list-search\"]").CountAsync();
            if (search > 0) return true;
            // Ancienne version : champ recherche = div[contenteditable=true][aria-label*="Rechercher"]
            var old = await page.Locator("div[contenteditable=\"true\"][aria-label*=\"Rechercher\"]").CountAsync();
            if (old > 0) return true;
            // QR présent = pas connecté
            var qr = await page.Locator("[data-testid=\"qrcode\"]").CountAsync();
            return qr == 0;
        }
        catch { return false; }
    }

    public async Task<WhatsAppResult> EnsureLoggedInAsync(CancellationToken ct)
    {
        var page = await GetOrOpenPageAsync(ct);
        await page.WaitForTimeoutAsync(1500);
        var loggedIn = await IsLoggedIn(page);
        if (loggedIn)
            return WhatsAppResult.Ok("WhatsApp Web connecté.");

        return WhatsAppResult.Fail(
            "WhatsApp Web non connecté : scanne le QR code dans le navigateur Jarvis (onglet WhatsApp) puis réessaie.");
    }

    private async Task<bool> OpenChatAsync(IPage page, string contact, CancellationToken ct)
    {
        // 1) champ de recherche
        string[] searchSelectors =
        {
            "[data-testid=\"chat-list-search\"]",
            "div[contenteditable=\"true\"][aria-label*=\"Rechercher\"]",
            "div[contenteditable=\"true\"][data-tab=\"3\"]"
        };
        var found = await WaitForElementAsync(page, searchSelectors, 8000, ct);
        if (!found) { _logger.LogWarning("[WhatsApp] Champ recherche introuvable"); return false; }

        foreach (var sel in searchSelectors)
        {
            try
            {
                var box = page.Locator(sel).First;
                if (await box.CountAsync() == 0) continue;
                await box.ClickAsync();
                await box.FillAsync(contact);
                break;
            }
            catch { }
        }
        await page.WaitForTimeoutAsync(900);

        // 2) clic sur la conversation correspondant au contact
        string[] chatRowSelectors =
        {
            $"div[role=\"listitem\"]:has-text(\"{contact}\")",
            $"div[role=\"option\"]:has-text(\"{contact}\")",
            $"span[title=\"{contact}\"]"
        };
        foreach (var sel in chatRowSelectors)
        {
            try
            {
                var row = page.Locator(sel).First;
                if (await row.CountAsync() > 0)
                {
                    await row.ClickAsync(new LocatorClickOptions { Timeout = 6000 });
                    await page.WaitForTimeoutAsync(1200);
                    return true;
                }
            }
            catch { }
        }
        _logger.LogWarning("[WhatsApp] Conversation '{Contact}' introuvable dans la liste", contact);
        return false;
    }

    public async Task<WhatsAppResult> SendTextAsync(string contact, string message, CancellationToken ct)
    {
        var page = await GetOrOpenPageAsync(ct);
        var login = await EnsureLoggedInAsync(ct);
        if (!login.Success) return login;

        if (string.IsNullOrWhiteSpace(contact) || string.IsNullOrWhiteSpace(message))
            return WhatsAppResult.Fail("Contact et message requis.");

        if (!await OpenChatAsync(page, contact, ct))
            return WhatsAppResult.Fail($"Conversation '{contact}' introuvable sur WhatsApp.");

        string[] inputSelectors =
        {
            "div[contenteditable=\"true\"][data-tab=\"10\"]",
            "div[contenteditable=\"true\"][role=\"textbox\"]",
            "[data-testid=\"conversation-compose-box-input\"]"
        };
        var ok = await WaitForElementAsync(page, inputSelectors, 8000, ct);
        if (!ok) return WhatsAppResult.Fail("Boîte de saisie WhatsApp introuvable.");

        foreach (var sel in inputSelectors)
        {
            try
            {
                var input = page.Locator(sel).First;
                if (await input.CountAsync() == 0) continue;
                await input.ClickAsync();
                await input.FillAsync(message);
                await page.Keyboard.PressAsync("Enter");
                await page.WaitForTimeoutAsync(700);
                _logger.LogInformation("[WhatsApp] Message envoyé à {Contact}", contact);
                return WhatsAppResult.Ok($"Message envoyé à {contact}.");
            }
            catch { }
        }
        return WhatsAppResult.Fail("Impossible d'envoyer le message (saisie échouée).");
    }

    public async Task<WhatsAppResult> ReadConversationAsync(string contact, int maxMessages, CancellationToken ct)
    {
        var page = await GetOrOpenPageAsync(ct);
        var login = await EnsureLoggedInAsync(ct);
        if (!login.Success) return login;

        if (!await OpenChatAsync(page, contact, ct))
            return WhatsAppResult.Fail($"Conversation '{contact}' introuvable.");

        try
        {
            await page.WaitForTimeoutAsync(900);
            var bubbles = page.Locator("div[data-testid=\"conversation-panel-messages\"] .message-in, div[data-testid=\"conversation-panel-messages\"] .message-out");
            var count = await bubbles.CountAsync();
            if (count == 0)
            {
                // fallback : sélecteur plus générique
                bubbles = page.Locator("div[role=\"row\"] [data-pre-plain-text]");
                count = await bubbles.CountAsync();
            }
            var take = Math.Min(maxMessages, count);
            var sb = new System.Text.StringBuilder();
            for (var i = count - take; i < count; i++)
            {
                try
                {
                    var bubble = bubbles.Nth(i);
                    var direction = (await bubble.GetAttributeAsync("data-testid"))?.Contains("message-out") == true ? "moi" : "interlocuteur";
                    var text = await bubble.InnerTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine($"{direction}: {text.Trim()}");
                }
                catch { }
            }
            if (sb.Length == 0)
                return WhatsAppResult.Ok("(conversation vide ou illisible : aucun message trouvé)");
            return WhatsAppResult.Ok(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WhatsApp] Lecture conversation échouée");
            return WhatsAppResult.Fail($"Lecture échouée : {ex.Message}");
        }
    }

    public async Task<WhatsAppResult> WaitForReplyAsync(string contact, TimeSpan timeout, CancellationToken ct)
    {
        var page = await GetOrOpenPageAsync(ct);
        var login = await EnsureLoggedInAsync(ct);
        if (!login.Success) return login;
        if (!await OpenChatAsync(page, contact, ct))
            return WhatsAppResult.Fail($"Conversation '{contact}' introuvable.");

        // Compte les messages existants, puis attend qu'un nouveau message-IN arrive.
        var baseCount = 0;
        try
        {
            baseCount = await GetIncomingCountAsync(page);
        }
        catch { }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastText = string.Empty;
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            await page.WaitForTimeoutAsync(700);
            try
            {
                var nowCount = await GetIncomingCountAsync(page);
                if (nowCount > baseCount)
                {
                    // remonte le dernier message reçu
                    var text = await GetLastIncomingAsync(page);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lastText = text;
                        return WhatsAppResult.Ok(text.Trim());
                    }
                }
                else if (nowCount > 0)
                {
                    // possiblement une réponse typée : on capture le dernier message même si le compteur n'a pas bougé
                    var text = await GetLastIncomingAsync(page);
                    if (!string.IsNullOrWhiteSpace(text) && text != lastText)
                    {
                        lastText = text;
                        return WhatsAppResult.Ok(text.Trim());
                    }
                }
            }
            catch { }
        }
        return WhatsAppResult.Fail($"Aucune réponse reçue après {(int)timeout.TotalSeconds}s.");
    }

    private static async Task<int> GetIncomingCountAsync(IPage page)
    {
        var incoming = page.Locator("div[data-testid=\"conversation-panel-messages\"] .message-in");
        return await incoming.CountAsync();
    }

    private async Task<string> GetLastIncomingAsync(IPage page)
    {
        var incoming = page.Locator("div[data-testid=\"conversation-panel-messages\"] .message-in");
        var count = await incoming.CountAsync();
        if (count == 0) return string.Empty;
        try { return (await incoming.Nth(count - 1).InnerTextAsync()).Trim(); }
        catch { return string.Empty; }
    }

    public async Task<WhatsAppResult> PlaceVoiceCallAsync(string contact, CancellationToken ct)
    {
        var page = await GetOrOpenPageAsync(ct);
        var login = await EnsureLoggedInAsync(ct);
        if (!login.Success) return login;
        if (!await OpenChatAsync(page, contact, ct))
            return WhatsAppResult.Fail($"Conversation '{contact}' introuvable.");

        string[] callButtons = { "[data-testid=\"call\"]", "[aria-label*=\"Appel\"]", "button[title=\"Appel\"]" };
        var ok = await WaitForElementAsync(page, callButtons, 8000, ct);
        if (!ok) return WhatsAppResult.Fail("Bouton d'appel vocal introuvable (WhatsApp Web).");

        foreach (var sel in callButtons)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.CountAsync() == 0) continue;
                await btn.ClickAsync(new LocatorClickOptions { Timeout = 6000 });
                await page.WaitForTimeoutAsync(1500);
                _logger.LogInformation("[WhatsApp] Appel vocal placé vers {Contact}", contact);
                return WhatsAppResult.Ok($"Appel vocal lancé vers {contact} (best-effort : l'audio passe par le haut-parleur du PC).");
            }
            catch { }
        }
        return WhatsAppResult.Fail("Impossible de déclencher l'appel vocal.");
    }

    public async Task<WhatsAppResult> HangUpCallAsync(CancellationToken ct)
    {
        var page = await GetOrOpenPageAsync(ct);
        string[] hangSelectors = { "[data-testid=\"call-hangup\"]", "[aria-label*=\"Raccrocher\"]", "button[aria-label*=\"Hang up\"]" };
        foreach (var sel in hangSelectors)
        {
            try
            {
                var btn = page.Locator(sel).First;
                if (await btn.CountAsync() > 0)
                {
                    await btn.ClickAsync(new LocatorClickOptions { Timeout = 4000 });
                    return WhatsAppResult.Ok("Appel raccroché.");
                }
            }
            catch { }
        }
        return WhatsAppResult.Fail("Aucun appel vocal en cours à raccrocher.");
    }
}
