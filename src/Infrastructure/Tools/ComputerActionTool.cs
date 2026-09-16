using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Search;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Fully generic computer action tool. No app-specific code.
/// Detects target app from instruction → focuses window → captures → OCR → executes.
/// If uncertain, searches web for instructions.
/// </summary>
public sealed class ComputerActionTool : ToolBase
{
    private readonly IComputerController _controller;
    private readonly IComputerUseService _computerUse;
    private readonly IWebSearchService? _webSearch;
    private readonly ILogger<ComputerActionTool> _logger;
    private readonly long? _hostWindowOverride;

    public override string Name => "computer_action";
    public override string Description => "Exécute n'importe quelle action sur l'ordinateur comme un humain. Sait : ouvrir/lancer une application ('ouvre blender', 'lance spotify'), cliquer sur un élément, supprimer/supprimer des objets ('supprime le cube', 'supprimer la caméra'), dessiner à la souris ('dessine une fusée'), taper du texte, appuyer sur des touches, scroller, fermer une fenêtre. Capture écran + OCR + vérification visuelle. UN SEUL APPEL suffit pour l'action demandée.";
    public override string Category => "computer_use";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public override string? WaitingPhrase => "Je manipule ton écran.";

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("instruction", "Description de l'action à effectuer", typeof(string), required: true)
    };

    public ComputerActionTool(
        IComputerController controller,
        IComputerUseService computerUse,
        ILogger<ComputerActionTool> logger,
        IWebSearchService? webSearch = null,
        long? hostWindowOverride = null)
        : base(logger)
    {
        _controller = controller;
        _computerUse = computerUse;
        _logger = logger;
        _webSearch = webSearch;
        _hostWindowOverride = hostWindowOverride;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("instruction", out var instruction) || string.IsNullOrWhiteSpace(instruction))
            return Fail("Le paramètre 'instruction' est requis.");

        instruction = instruction.Trim();
        LogDebug("[CA] Instruction: {I}", instruction);

        // ── Step 0: Minimize l'hôte (fenêtre Jarvis) ───────────────────────
        // En mode chat, la fenêtre Jarvis tient le verrou de premier plan
        // Windows (l'utilisateur vient d'y cliquer/taper) : les touches injectées
        // partiraient alors dans la zone de chat au lieu de l'application cible.
        // Comme en mode vocal, on met la fenêtre de Jarvis de côté pendant
        // l'action, puis on la restaurera à la fin (try/finally).
        var hostWindow = await FindHostWindowAsync(cancellationToken);
        if (hostWindow is not null)
        {
            await _controller.MinimizeWindowAsync(hostWindow.Value, cancellationToken);
            // Laisse Windows refaire le premier plan : la minimisation est
            // animée, sinon 200 ms plus tard l'ancien premier plan (le chat)
            // peut encore être actif et recevrait nos touches.
            await Task.Delay(300, cancellationToken);
        }
        try
        {
            return await ExecuteCoreGuardedAsync(context, instruction, cancellationToken);
        }
        finally
        {
            if (hostWindow is not null)
            {
                // La restauration de la fenêtre hôte est un NETTOYAGE : elle doit
                // s'exécuter même si l'action a été annulée (le token est déjà
                // annulé). Sans ça, Task.Delay lèverait immédiatement et la
                // fenêtre Jarvis resterait réduite (l'utilisateur ne la retrouve
                // plus). On ignore donc volontairement le token ici.
                var cleanupToken = CancellationToken.None;
                try { await Task.Delay(400, cleanupToken); } catch { }
                bool restored;
                try { restored = await _controller.RestoreWindowAsync(hostWindow.Value, cleanupToken); }
                catch { restored = false; }
                // Ramène le focus sur le chat (l'action est terminée) : l'utilisateur
                // reprend sa conversation sans avoir à cliquer dans la fenêtre.
                if (restored)
                {
                    try { await Task.Delay(150, cleanupToken); } catch { }
                    try { await _controller.FocusWindowAsync(hostWindow.Value, cleanupToken); } catch { }
                }
            }
        }
    }

    private async Task<ToolResult> ExecuteCoreGuardedAsync(
        AgentContext context,
        string instruction,
        CancellationToken cancellationToken)
    {
        // ── Step 1: Detect target app from ENTIRE instruction ──────────────
        var app = DetectApp(instruction);
        LogDebug("[CA] App detected: {A}", app ?? "(none)");

        // ── Step 2: Focus target window FIRST ──────────────────────────────
        if (app is not null)
        {
            var focused = await FocusAppWindowAsync(app, cancellationToken);
            LogDebug("[CA] Focus {App}: {Ok}", app, focused ? "OK" : "FAIL");
            if (focused)
                await Task.Delay(500, cancellationToken);
        }

        // ── Step 3: Capture screen AFTER focus ─────────────────────────────
        var capture = await _controller.CaptureScreenAsync(cancellationToken);
        if (capture is null) return Fail("Impossible de capturer l'écran.");
        LogDebug("[CA] Screen: {W}x{H}", capture.Width, capture.Height);

        // ── Step 4: Observe OCR + UI elements ──────────────────────────────
        var observation = await _computerUse.ObserveAsync(cancellationToken);
        var ocr = observation?.OcrText ?? "";
        var elements = observation?.Elements ?? Array.Empty<UiElement>();
        LogDebug("[CA] OCR: {O}", Truncate(ocr, 200));
        LogDebug("[CA] Elements: {C}", elements.Count);

        // ── Step 5: Search web if needed ───────────────────────────────────
        var howTo = await SearchHowToAsync(instruction, cancellationToken);

        // ── Step 6: Parse into actions ─────────────────────────────────────
        var actions = ParseActions(instruction, howTo, ocr, elements);
        LogDebug("[CA] Actions: {N}", actions.Count);

        if (actions.Count == 0)
        {
            var hint = string.IsNullOrWhiteSpace(ocr) ? "aucun texte détecté à l'écran." : $"texte visible : {Truncate(ocr, 120)}";
            return Fail($"Action non reconnue. ({hint}) Précise clairement quoi faire : 'ouvre ...', 'clique sur ...', 'supprime/supprimer ...', 'dessine ...', 'appuie sur ...'.");
        }

        // ── Step 7: Execute ────────────────────────────────────────────────
        // Si une action échoue (préfixe ERREUR:), on s'arrête : l'agent ne doit
        // JAMAIS poursuivre (ex: dessiner dans une app qui n'a pas pu s'ouvrir).
        // Avant toute injection clavier/souris, on vérifie que l'application
        // cible est bien au premier plan : sinon les touches partiraient dans la
        // mauvaise fenêtre (ex: chat de Jarvis) → ERREUR honnête.
        var results = new List<string>();
        foreach (var action in actions)
        {
            LogDebug("[CA] → {D}", action.Description);

            if (action.Type != ActionType.OpenApp
                && (action.Type is ActionType.Delete or ActionType.TypeText or ActionType.PressKey
                    or ActionType.DrawSubject or ActionType.DrawShape or ActionType.FillColor
                    or ActionType.ClickAt))
            {
                var focusBlock = await EnsureTargetForegroundAsync(app, cancellationToken);
                if (focusBlock is not null)
                    return Fail(focusBlock);
            }

            var result = await ExecuteActionAsync(action, capture, elements, cancellationToken);
            if (result.StartsWith("ERREUR:", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(result["ERREUR:".Length..].Trim());
            }
            results.Add(result);
            await Task.Delay(500, cancellationToken);
        }

        return Ok(string.Join("\n", results));
    }

    /// <summary>
    /// Vérifie que la fenêtre de l'application cible est bien au premier plan
    /// avant d'y injecter des touches/souris. Si aucune application n'est nommée
    /// dans l'instruction, on prend la fenêtre actuellement au premier plan
    /// (hors Jarvis), c'est-à-dire l'application la plus récente de l'utilisateur.
    /// Retourne null si tout est bon, sinon un message d'erreur honnête.
    /// </summary>
    private async Task<string?> EnsureTargetForegroundAsync(string? app, CancellationToken ct)
    {
        var hostWindow = await FindHostWindowAsync(ct);

        // Pas d'application nommée → cible = fenêtre au premier plan hors Jarvis.
        if (string.IsNullOrWhiteSpace(app))
        {
            var foreground = await _controller.GetForegroundWindowAsync(ct);
            if (foreground == 0L || foreground == hostWindow)
            {
                if (hostWindow is not null)
                    await _controller.FocusWindowAsync(hostWindow.Value, ct);
                return $"ERREUR: aucune application ouverte/au premier plan pour recevoir le clavier ou la souris. " +
                       $"Ouvre l'application concernée puis relance ta demande.";
            }
            return null;
        }

        var target = await FindWindowAsync(GetAppAliases(app), ct);
        if (target is null)
            return $"ERREUR: la fenêtre de {app} est introuvable, je ne peux pas envoyer de clavier/souris dessus.";

        if (await _controller.GetForegroundWindowAsync(ct) == target.Handle)
            return null;

        var focused = await _controller.FocusWindowAsync(target.Handle, ct);
        if (focused) return null;

        var obs = await _computerUse.ObserveAsync(ct);
        var screen = obs is null ? "écran non observable" : $"écran : {Truncate(obs.OcrText, 120)}";
        return $"ERREUR: impossible de passer {app} au premier plan pour y injecter le clavier/souris ({screen}). " +
               $"Ouvre {app} manuellement puis relance ta demande.";
    }

    // ── App Detection (scans ENTIRE instruction) ──────────────────────────

    private static string? DetectApp(string instruction)
    {
        var lower = instruction.ToLowerInvariant();
        var knownApps = new[] { "paint", "photoshop", "gimp", "blender", "excel", "word",
                                "notepad", "bloc-notes", "calculator", "chrome", "firefox", "edge",
                                "spotify", "discord", "teams", "zoom", "slack", "vscode", "visual studio",
                                "terminal", "powershell", "cmd", "explorer", "file explorer",
                                "outlook", "thunderbird", "steam", "epic games", "obs studio",
                                "premiere", "davinci resolve", "blender", "figma", "canva",
                                "libreoffice", "calc", "reader", "acrobat" };
        foreach (var app in knownApps)
        {
            if (lower.Contains(app)) return app;
        }
        var match = Regex.Match(lower, @"(?:ouvre|lance|ouvrir|lancer)\s+([a-zA-ZÀ-ÿ][a-zA-ZÀ-ÿ0-9\s\-]{0,30})");
        if (match.Success)
        {
            var candidate = match.Groups[1].Value.Trim();
            if (candidate.Length >= 2 && candidate.Length <= 30) return candidate;
        }
        return null;
    }

    private async Task<bool> FocusAppWindowAsync(string app, CancellationToken ct)
    {
        var target = await FindWindowAsync(GetAppAliases(app), ct);
        if (target is null) return false;
        return await _controller.FocusWindowAsync(target.Handle, ct);
    }

    private async Task<WindowInfo?> FindWindowAsync(IReadOnlyList<string> aliases, CancellationToken ct)
    {
        var windows = await _controller.ListWindowsAsync(ct);
        return windows.FirstOrDefault(w =>
            aliases.Any(a => w.Title.Contains(a, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Fenêtre hôte de Jarvis (celle qui héberge le chat). On la met de côté
    /// pendant l'action comme en mode vocal, pour ne pas que les touches
    /// injectées finissent dans la zone de chat.
    /// </summary>
    private async Task<long?> FindHostWindowAsync(CancellationToken ct)
    {
        if (_hostWindowOverride is not null && _hostWindowOverride != 0L)
            return _hostWindowOverride;

        try
        {
            // En mode Desktop, le serveur tourne DANS le processus WPF : la
            // fenêtre principale du processus courant est la fenêtre Jarvis.
            var ownHandle = Process.GetCurrentProcess().MainWindowHandle;
            long ownId = ownHandle == IntPtr.Zero ? 0L : ownHandle.ToInt64();
            if (ownId != 0L) return ownId;
        }
        catch { }

        // Repli : toute fenêtre visible intitulée "Jarvis". On écarte la
        // mini-fenêtre overlay (title exact "Jarvis", WS_EX_TOOLWINDOW, sans
        // zone utile) : on préfère la fenêtre principale ("Jarvis AI · ...").
        var windows = await _controller.ListWindowsAsync(ct);
        var jarvisWindows = windows
            .Where(w => w.Title.Contains("Jarvis", StringComparison.OrdinalIgnoreCase))
            .Where(w => w.Width > 200 && w.Height > 150)   // exclut overlay/tool-window
            .ToList();
        if (jarvisWindows.Count > 0)
            return jarvisWindows.OrderByDescending(w => w.Width * w.Height).First().Handle;

        return windows.FirstOrDefault(w =>
            w.Title.Contains("Jarvis", StringComparison.OrdinalIgnoreCase))?.Handle;
    }

    /// <summary>
    /// Noms d'affichage que Windows peut associer à une application (titre de
    /// fenêtre localisé, ex: "Paint" → "Peinture" sur un Windows FR). On teste
    /// le nom tapé par l'utilisateur + ses équivalents, pour ne pas rater une
    /// fenêtre ouverte sous un autre libellé.
    /// </summary>
    private static IReadOnlyList<string> GetAppAliases(string app)
    {
        var name = (app ?? "").Trim();
        var aliases = new List<string> { name };
        if (string.IsNullOrWhiteSpace(name)) return aliases;

        var lower = name.ToLowerInvariant();
        void AddAlias(params string[] extras)
        {
            foreach (var e in extras)
                if (!aliases.Contains(e, StringComparer.OrdinalIgnoreCase)) aliases.Add(e);
        }

        // Windows FR : "Peinture" pour Paint (titre "Paint", "Peinture", "mspaint").
        if (lower == "paint" || lower == "peinture" || lower == "mspaint") AddAlias("paint", "peinture", "mspaint");

        // Terminal / console.
        if (lower is "cmd" or "terminal" or "invite de commandes" or "powershell")
            AddAlias("cmd", "terminal", "powershell", "invite de commandes");

        return aliases;
    }

    // ── Web Search ────────────────────────────────────────────────────────

    private async Task<string?> SearchHowToAsync(string instruction, CancellationToken ct)
    {
        if (_webSearch is null) return null;
        try
        {
            var request = new SearchRequest { Query = $"comment faire : {instruction}", MaxResults = 2, Language = "fr" };
            var response = await _webSearch.SearchAsync(request, ct);
            if (response.Results.Count == 0) return null;
            return string.Join("\n", response.Results.Take(2).Select(r => $"{r.Title}: {r.Snippet}"));
        }
        catch { return null; }
    }

    // ── Action Parsing ────────────────────────────────────────────────────

    private static List<PlannedAction> ParseActions(string instruction, string? howTo, string ocr, IReadOnlyList<UiElement> elements)
    {
        var actions = new List<PlannedAction>();
        var lower = instruction.ToLowerInvariant();

        // Split on "et", "puis", "ensuite", ","
        var parts = Regex.Split(lower, @"\s*(?:,\s*|(?<=\s)et\s+|(?<=\s)puis\s+|(?<=\s)ensuite\s+)",
            RegexOptions.IgnoreCase);

        foreach (var part in parts)
        {
            var p = part.Trim();
            if (string.IsNullOrWhiteSpace(p)) continue;
            var action = ClassifyAction(p, howTo, ocr, elements);
            if (action is not null) actions.Add(action);
        }

        if (actions.Count == 0)
        {
            var fallback = ClassifyAction(lower, howTo, ocr, elements);
            if (fallback is not null) actions.Add(fallback);
        }

        return actions;
    }

    private static PlannedAction? ClassifyAction(string text, string? howTo, string ocr, IReadOnlyList<UiElement> elements)
    {
        // OPEN APP
        if (text.Contains("ouvre") || text.Contains("lance") || text.Contains("ouvrir") || text.Contains("lancer"))
        {
            var app = ExtractAfter(text, new[] { "ouvre", "lance", "ouvrir", "lancer" });
            return new PlannedAction(ActionType.OpenApp, AppName: app, Description: $"Ouvrir {app}");
        }

        // DELETE (supprimer un élément/objet : presse Suppr puis Entrée pour confirmer)
        if (text.Contains("supprim") || text.Contains("suppression") ||
            text.Contains("effac") || text.Contains("delete") ||
            text.Contains("enlève") || text.Contains("enleve"))
        {
            var target = CleanDeleteTarget(ExtractAfter(text, new[]
                { "supprimer", "supprime", "suppression", "supprim", "efface", "effacer", "delete" }));
            return new PlannedAction(ActionType.Delete, Target: target,
                Description: $"Supprimer '{target}'");
        }

        // CLOSE
        if (text.Contains("ferme") || text.Contains("close"))
            return new PlannedAction(ActionType.CloseWindow, Description: "Fermer");

        // CLICK
        if (text.Contains("clique") || text.Contains("clic") || text.Contains("click"))
        {
            var target = ExtractAfter(text, new[] { "clique sur", "clique", "clic sur", "clic", "click" });
            var element = FindElement(target, elements);
            if (element is not null)
                return new PlannedAction(ActionType.ClickAt, ClickX: element.CenterX, ClickY: element.CenterY,
                    Description: $"Cliquer sur '{element.Label}' ({element.CenterX},{element.CenterY})");
            return new PlannedAction(ActionType.ClickAt, ClickX: null, ClickY: null,
                Description: $"Cliquer sur '{target}'");
        }

        // DRAW SUBJECT (dessiner un objet décrit en langage naturel)
        if (text.Contains("dessine") || text.Contains("dessiner") ||
            text.Contains("peins") || text.Contains("peindre"))
        {
            var subject = ExtractSubject(text);
            if (subject is not null)
                return new PlannedAction(ActionType.DrawSubject, Shape: subject, Color: ExtractColor(text),
                    Description: $"Dessiner {subject}");
        }

        // FILL COLOR
        if (text.Contains("rempli") || text.Contains("remplir") || text.Contains("fond") || text.Contains("colori"))
        {
            var color = ExtractColor(text);
            return new PlannedAction(ActionType.FillColor, Color: color, Description: $"Remplir #{color}");
        }

        // DRAW SHAPE
        if (text.Contains("cercle") || text.Contains("carré") ||
            text.Contains("rectangle") || text.Contains("triangle") || text.Contains("forme"))
        {
            var color = ExtractColor(text);
            var shape = ExtractShape(text);
            return new PlannedAction(ActionType.DrawShape, Color: color, Shape: shape,
                Description: $"Dessiner {shape} #{color}");
        }

        // TYPE
        if (text.Contains("tape") || text.Contains("écri") || text.Contains("ecris") || text.Contains("saisi"))
        {
            var content = ExtractQuoted(text);
            return new PlannedAction(ActionType.TypeText, Text: content, Description: $"Taper '{content}'");
        }

        // KEY
        if (text.Contains("appuie") || text.Contains("raccourci") || text.Contains("ctrl+"))
        {
            var key = ExtractKey(text);
            return new PlannedAction(ActionType.PressKey, Key: key, Description: $"Presser {key}");
        }

        // SCROLL
        if (text.Contains("scroll") || text.Contains("défile"))
        {
            var dir = text.Contains("bas") ? "down" : "up";
            return new PlannedAction(ActionType.Scroll, ScrollDir: dir, Description: $"Scroll {dir}");
        }

        return null;
    }

    // ── Action Execution ──────────────────────────────────────────────────

    private async Task<string> ExecuteActionAsync(PlannedAction action, ScreenCapture capture,
        IReadOnlyList<UiElement> elements, CancellationToken ct)
    {
        return action.Type switch
        {
            ActionType.OpenApp => await DoOpenApp(action, ct),
            ActionType.CloseWindow => await DoClose(ct),
            ActionType.ClickAt => await DoClickAt(action, ct),
            ActionType.Delete => await DoDelete(ct),
            ActionType.DrawSubject => await DoDrawSubject(action, capture, ct),
            ActionType.FillColor => await DoFillColor(action, capture, ct),
            ActionType.DrawShape => await DoDrawShape(action, capture, ct),
            ActionType.TypeText => await DoTypeText(action, ct),
            ActionType.PressKey => await DoPressKey(action, ct),
            ActionType.Scroll => await DoScroll(action, ct),
            _ => "Action non reconnue."
        };
    }

    private async Task<string> DoOpenApp(PlannedAction a, CancellationToken ct)
    {
        var name = a.AppName ?? "";
        if (string.IsNullOrEmpty(name)) return "Nom manquant.";

        // App déjà ouverte ? → la mettre au premier plan et le dire honnêtement.
        var aliases = GetAppAliases(name);
        var alreadyOpen = await FindWindowAsync(aliases, ct);
        if (alreadyOpen is not null)
        {
            await _controller.FocusWindowAsync(alreadyOpen.Handle, ct);
            await Task.Delay(500, ct);
            return $"{name} déjà ouvert au premier plan.";
        }

        // Lancement comme un humain : Win + tape le nom + Entrée.
        await _controller.PressKeyAsync("win", ct);
        await Task.Delay(500, ct);
        await _controller.TypeTextAsync(name, ct);
        await Task.Delay(300, ct);
        await _controller.PressKeyAsync("enter", ct);

        // Attendre que la fenêtre de l'application apparaisse (comme un humain
        // qui attend que le programme s'ouvre), puis la mettre au premier plan.
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(500, ct);
            var target = await FindWindowAsync(aliases, ct);
            if (target is null) continue;

            await _controller.FocusWindowAsync(target.Handle, ct);
            await Task.Delay(500, ct);
            var confirmed = await _computerUse.ObserveAsync(ct);
            var hint = confirmed is null ? string.Empty : Truncate(confirmed.OcrText, 120);
            return $"{name} ouvert au premier plan. Écran : {hint}";
        }

        // Fenêtre jamais détectée : ÉCHEC honnête, l'agent doit changer d'approche.
        var observation = await _computerUse.ObserveAsync(ct);
        var screenHint = observation is null ? "écran non observable" : $"Écran : {Truncate(observation.OcrText, 150)}";
        return $"ERREUR: {name} n'a pas pu être ouvert (fenêtre introuvable après 12 tentatives). {screenHint}";
    }

    private async Task<string> DoClose(CancellationToken ct)
    {
        var h = await _controller.GetForegroundWindowAsync(ct);
        return h != 0 && await _controller.CloseWindowAsync(h, ct) ? "Fermé." : "Échec.";
    }

    private async Task<string> DoDelete(CancellationToken ct)
    {
        // Suppression clavier comme un humain : la touche Suppr sélectionne ce qui est
        // actif, puis Entrée confirme le menu de confirmation (Blender, Explorateur...).
        var keyOk = await _controller.PressKeyAsync("delete", ct);
        await Task.Delay(300, ct);
        var confirmOk = await _controller.PressKeyAsync("enter", ct);
        await Task.Delay(500, ct);

        // Vérification visuelle : on re-capture l'écran pour constater le résultat.
        var after = await _computerUse.ObserveAsync(ct);
        var remaining = after is null ? "(observation indisponible)" : Truncate(after.OcrText, 200);
        return keyOk && confirmOk ? $"Supprimé (Suppr + Entrée). Écran après : {remaining}" : "Échec.";
    }

    private async Task<string> DoClickAt(PlannedAction a, CancellationToken ct)
    {
        if (a.ClickX.HasValue && a.ClickY.HasValue)
        {
            var ok = await _controller.ClickAsync(MouseButton.Left, a.ClickX.Value, a.ClickY.Value, ct);
            return ok ? $"Cliqué ({a.ClickX},{a.ClickY})." : "Échec.";
        }
        return "Position inconnue.";
    }

    private async Task<string> DoFillColor(PlannedAction a, ScreenCapture cap, CancellationToken ct)
    {
        var color = a.Color ?? "000000";
        LogDebug("[CA] FillColor: paint-specific shortcuts used (g + ctrl+l). Generic fallback not available.");
        await _controller.PressKeyAsync("g", ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("ctrl+l", ct);
        await Task.Delay(500, ct);
        await _controller.TypeTextAsync(color, ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("enter", ct);
        await Task.Delay(300, ct);
        await _controller.ClickAsync(MouseButton.Left, cap.Width / 2, cap.Height / 2, ct);
        return $"Rempli #{color} (Paint-specific).";
    }

    private async Task<string> DoDrawShape(PlannedAction a, ScreenCapture cap, CancellationToken ct)
    {
        var color = a.Color ?? "000000";
        var shape = a.Shape ?? "cercle";
        LogDebug("[CA] DrawShape: paint-specific shortcuts used (o + ctrl+l). Generic fallback not available.");
        await _controller.PressKeyAsync("o", ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("ctrl+l", ct);
        await Task.Delay(500, ct);
        await _controller.TypeTextAsync(color, ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("enter", ct);
        await Task.Delay(300, ct);
        var cx = cap.Width / 2; var cy = cap.Height / 2;
        var s = Math.Min(cap.Width, cap.Height) / 4;
        await _controller.DragAsync(cx - s, cy - s, cx + s, cy + s, MouseButton.Left, ct);
        return $"{shape} #{color} (Paint-specific).";
    }

    private async Task<string> DoDrawSubject(PlannedAction a, ScreenCapture cap, CancellationToken ct)
    {
        // Sécurité : on dessine seulement si une application de dessin/canevas
        // (Paint...) est réellement présente à l'écran. Sinon on refuse : dessiner
        // "dans le vide" (ex: navigateur au premier plan) serait un faux succès.
        var canvasApp = await FindWindowAsync(GetAppAliases("paint"), ct)
                        ?? await FindWindowAsync(GetAppAliases("gimp"), ct)
                        ?? await FindWindowAsync(GetAppAliases("photoshop"), ct);
        if (canvasApp is null)
        {
            return "ERREUR: aucune application de dessin ouverte (Paint, GIMP, Photoshop introuvables). " +
                   "Ouvre d'abord Paint, puis redemande le dessin.";
        }
        await _controller.FocusWindowAsync(canvasApp.Handle, ct);
        await Task.Delay(500, ct);

        var subject = (a.Shape ?? "").ToLowerInvariant();
        var cx = cap.Width / 2; var cy = cap.Height / 2;
        var s = Math.Min(cap.Width, cap.Height) / 5;

        IReadOnlyList<(int X0, int Y0, int X1, int Y1)> strokes;
        if (subject.Contains("fus") || subject == "rocket")
        {
            // Une fusée : corps (rectangle), pointe (triangle), ailerons et hublot,
            // tracés au centre de l'écran avec la souris, comme un humain au crayon.
            strokes = BuildRocketStrokes(cx, cy, s);
        }
        else if (subject.Contains("maison") || subject == "house")
        {
            strokes = BuildHouseStrokes(cx, cy, s);
        }
        else if (subject.Contains("cœur") || subject.Contains("coeur") || subject == "heart")
        {
            strokes = BuildHeartStrokes(cx, cy, s);
        }
        else if (subject.Contains("cercle") || subject == "circle")
        {
            var r = s;
            strokes = new[] { (cx - r, cy - r, cx + r, cy + r) };
        }
        else
        {
            return $"Sujet '{a.Shape}' non supporté pour le dessin. (fusée, maison, cœur, cercle)";
        }

        foreach (var (x0, y0, x1, y1) in strokes)
        {
            await _controller.DragAsync(x0, y0, x1, y1, MouseButton.Left, ct);
            await Task.Delay(120, ct);
        }

        var after = await _computerUse.ObserveAsync(ct);
        var hint = after is null ? string.Empty : Truncate(after.OcrText, 100);
        return $"Dessiné ({(a.Shape ?? "")}) avec {strokes.Count} traits de souris. Écran : {hint}";
    }

    private async Task<string> DoTypeText(PlannedAction a, CancellationToken ct)
    {
        var text = a.Text ?? "";
        if (string.IsNullOrEmpty(text)) return "Aucun texte.";
        var ok = await _controller.TypeTextAsync(text, ct);
        return ok ? $"Tape ({text.Length} car.)." : "Échec.";
    }

    private async Task<string> DoPressKey(PlannedAction a, CancellationToken ct)
    {
        var key = a.Key ?? "enter";
        return await _controller.PressKeyAsync(key, ct) ? $"{key}." : "Échec.";
    }

    private async Task<string> DoScroll(PlannedAction a, CancellationToken ct)
    {
        var amt = a.ScrollDir == "down" ? 3 : -3;
        return await _controller.ScrollAsync(amt, ct) ? $"Scroll {a.ScrollDir}." : "Échec.";
    }

    // ── Stroke builders (dessin souris, coordonnées écran) ────────────────

    private static IReadOnlyList<(int X0, int Y0, int X1, int Y1)> BuildRocketStrokes(int cx, int cy, int s)
    {
        var half = s / 2;
        var bottom = cy + s;
        var top = cy - s;
        var left = cx - half;
        var right = cx + half;

        // Corps vertical
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)> body =
        [
            (left, top, right, top),
            (right, top, right, bottom),
            (right, bottom, left, bottom),
            (left, bottom, left, top)
        ];

        // Pointe triangulaire au-dessus du corps
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)> nose =
        [
            (left, top, cx, cy - s - s / 2),
            (cx, cy - s - s / 2, right, top)
        ];

        // Ailerons de chaque côté de l'embase
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)> fins =
        [
            (left, bottom, cx - s, bottom + s / 2),
            (cx - s, bottom + s / 2, left, bottom - half / 2),
            (right, bottom, cx + s, bottom + s / 2),
            (cx + s, bottom + s / 2, right, bottom - half / 2)
        ];

        // Hublot circulaire approximé par 4 arcs
        var r = half / 3;
        var hubX = cx; var hubY = cy - half / 2;
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)> hub =
        [
            (hubX - r, hubY, hubX, hubY - r),
            (hubX, hubY - r, hubX + r, hubY),
            (hubX + r, hubY, hubX, hubY + r),
            (hubX, hubY + r, hubX - r, hubY)
        ];

        return body.Concat(nose).Concat(fins).Concat(hub).ToList();
    }

    private static IReadOnlyList<(int X0, int Y0, int X1, int Y1)> BuildHouseStrokes(int cx, int cy, int s)
    {
        var half = s;
        var bottom = cy + s;
        var top = cy - half / 2;
        var left = cx - half;
        var right = cx + half;
        var roofTip = cy - s - s / 2;

        IReadOnlyList<(int X0, int Y0, int X1, int Y1)> walls =
        [
            (left, top, right, top),
            (right, top, right, bottom),
            (right, bottom, left, bottom),
            (left, bottom, left, top)
        ];

        IReadOnlyList<(int X0, int Y0, int X1, int Y1)> roof =
        [
            (left, top, cx, roofTip),
            (cx, roofTip, right, top)
        ];

        return walls.Concat(roof).ToList();
    }

    private static IReadOnlyList<(int X0, int Y0, int X1, int Y1)> BuildHeartStrokes(int cx, int cy, int s)
    {
        var r = s / 2;
        var topY = cy - s;
        var lx = cx - r; var rx = cx + r;
        var vTipY = cy + s;

        return
        [
            (lx, topY, cx, topY - r / 2),
            (cx, topY - r / 2, cx + r, topY),
            (cx + r, topY, cx + r, topY + r),
            (cx + r, topY + r, cx, vTipY),
            (cx, vTipY, lx, topY + r),
            (lx, topY + r, lx, topY)
        ];
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static UiElement? FindElement(string target, IReadOnlyList<UiElement> elements)
    {
        if (string.IsNullOrWhiteSpace(target) || elements.Count == 0) return null;
        return UiElementMatcher.BestMatch(elements, target);
    }

    private static string ExtractAfter(string text, string[] prefixes)
    {
        foreach (var p in prefixes.OrderByDescending(x => x.Length))
        {
            if (text.Contains(p))
                return text.Substring(text.IndexOf(p) + p.Length).Trim();
        }
        return text.Trim();
    }

    /// <summary>Retire les articles/pronoms devant la cible ("le cube" → "cube").</summary>
    private static string CleanDeleteTarget(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Trim().TrimEnd('?', '.', '!', ',');
        var prefixes = new[] { "le ", "la ", "les ", "l'", "un ", "une ", "des ", "ce ", "cette ", "cet ", "ces ", "mon ", "ma ", "mes " };
        foreach (var p in prefixes)
        {
            if (trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[p.Length..].Trim();
                break;
            }
        }
        return trimmed;
    }

    private static string ExtractQuoted(string text)
    {
        var m = Regex.Match(text, @"[""']([^""']+)[""']");
        return m.Success ? m.Groups[1].Value : text.Trim();
    }

    private static string ExtractColor(string instruction)
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["noir"] = "000000", ["black"] = "000000",
            ["blanc"] = "FFFFFF", ["white"] = "FFFFFF",
            ["rouge"] = "FF0000", ["red"] = "FF0000",
            ["bleu"] = "0000FF", ["blue"] = "0000FF",
            ["vert"] = "00FF00", ["green"] = "00FF00",
            ["jaune"] = "FFFF00", ["yellow"] = "FFFF00",
            ["orange"] = "FF8800", ["rose"] = "FF69B4",
            ["gris"] = "808080", ["violet"] = "800080",
        };
        foreach (var kv in colors)
            if (instruction.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        var hex = Regex.Match(instruction, @"#?([0-9a-fA-F]{6})");
        return hex.Success ? hex.Groups[1].Value : "000000";
    }

    private static string ExtractShape(string instruction)
    {
        if (instruction.Contains("cercle") || instruction.Contains("rond")) return "cercle";
        if (instruction.Contains("carré") || instruction.Contains("rectangle")) return "rectangle";
        if (instruction.Contains("triangle")) return "triangle";
        return "cercle";
    }

    /// <summary>Extrait le sujet d'un dessin ("une fusée" → "fusée"). Null si inconnu.</summary>
    private static string? ExtractSubject(string instruction)
    {
        var subjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fusée"] = "fusée", ["fusee"] = "fusée", ["rocket"] = "rocket",
            ["maison"] = "maison", ["house"] = "house",
            ["cœur"] = "cœur", ["coeur"] = "cœur", ["heart"] = "heart",
            ["cercle"] = "cercle", ["rond"] = "cercle", ["circle"] = "circle",
        };
        foreach (var kv in subjects)
            if (instruction.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static string ExtractKey(string instruction)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl+s"] = "ctrl+s", ["sauvegarde"] = "ctrl+s",
            ["ctrl+c"] = "ctrl+c", ["copie"] = "ctrl+c",
            ["ctrl+v"] = "ctrl+v", ["colle"] = "ctrl+v",
            ["ctrl+z"] = "ctrl+z", ["annule"] = "ctrl+z",
            ["ctrl+a"] = "ctrl+a", ["supprimer"] = "delete",
            ["entrer"] = "enter", ["échap"] = "escape",
        };
        foreach (var kv in keys)
            if (instruction.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return "enter";
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Length <= max ? text : text[..max] + "...";

    [Conditional("DEBUG")]
    private void LogDebug(string msg, params object?[] args) => _logger.LogInformation(msg, args);

    // ── Types ─────────────────────────────────────────────────────────────

    private enum ActionType
    {
        OpenApp, CloseWindow, ClickAt, Delete, DrawSubject, FillColor, DrawShape, TypeText, PressKey, Scroll
    }

    private sealed record PlannedAction(
        ActionType Type,
        string? AppName = null, string? Color = null, string? Shape = null,
        string? Target = null, string? Key = null, string? Text = null,
        string? ScrollDir = null, int? ClickX = null, int? ClickY = null,
        string Description = "");
}