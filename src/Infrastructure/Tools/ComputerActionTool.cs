using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Search;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
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
    public override string Description => "Exécute n'importe quelle action sur l'ordinateur comme un humain. Sait : ouvrir/lancer TOUTE application ('ouvre blender', 'lance spotify', 'ouvre le terminal', 'ouvre cmd'), cliquer sur un élément, supprimer/supprimer des objets ('supprime le cube', 'supprimer la caméra'), dessiner à la souris ('dessine une fusée'), taper du texte, appuyer sur des touches, scroller, fermer une fenêtre. Capture écran + OCR + vérification visuelle. UN SEUL APPEL suffit pour l'action demandée. C'est OUTIL PRINCIPAL pour TOUTE action PC.";
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
                try { await Task.Delay(200, cleanupToken); } catch { }
                bool restored;
                try { restored = await _controller.RestoreWindowAsync(hostWindow.Value, cleanupToken); }
                catch { restored = false; }
                // Ramène le focus sur le chat (l'action est terminée) : l'utilisateur
                // reprend sa conversation sans avoir à cliquer dans la fenêtre.
                if (restored)
                {
                    try { await Task.Delay(100, cleanupToken); } catch { }
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
                await Task.Delay(200, cancellationToken);
        }

        // ── Step 3: Observe OCR + UI elements (capture intégrée) ───────────
        // ObserveAsync effectue sa propre capture : pas de CaptureScreenAsync
        // séparée pour éviter de doubler le temps de capture (~200-500 ms).
        var observation = await _computerUse.ObserveAsync(cancellationToken);
        var ocr = observation?.OcrText ?? "";
        var elements = observation?.Elements ?? Array.Empty<UiElement>();
        LogDebug("[CA] Screen: {W}x{H}", observation?.ScreenWidth ?? 0, observation?.ScreenHeight ?? 0);
        LogDebug("[CA] OCR: {O}", Truncate(ocr, 200));
        LogDebug("[CA] Elements: {C}", elements.Count);

        // ── Step 5: Search web only for complex/unknown instructions ─────────
        string? howTo = null;
        var lowerInstr = instruction.ToLowerInvariant();
        // Skip search for simple known actions (open, click, type, delete, draw, etc.)
        var isSimpleAction = lowerInstr.Contains("ouvre") || lowerInstr.Contains("lance") ||
            lowerInstr.Contains("clique") || lowerInstr.Contains("clic") || lowerInstr.Contains("click") ||
            lowerInstr.Contains("tape") || lowerInstr.Contains("écri") || lowerInstr.Contains("ecris") ||
            lowerInstr.Contains("supprim") || lowerInstr.Contains("effac") || lowerInstr.Contains("delete") ||
            lowerInstr.Contains("dessine") || lowerInstr.Contains("peins") || lowerInstr.Contains("peindre") ||
            lowerInstr.Contains("rempli") || lowerInstr.Contains("remplir") || lowerInstr.Contains("colori") ||
            lowerInstr.Contains("ferme") || lowerInstr.Contains("close") ||
            lowerInstr.Contains("cercle") || lowerInstr.Contains("carré") || lowerInstr.Contains("rectangle") ||
            lowerInstr.Contains("triangle") || lowerInstr.Contains("forme") ||
            lowerInstr.Contains("appuie") || lowerInstr.Contains("pressionne");
        if (!isSimpleAction)
            howTo = await SearchHowToAsync(instruction, cancellationToken);

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

            var result = await ExecuteActionAsync(action, observation, elements, cancellationToken);
            if (result.StartsWith("ERREUR:", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(result["ERREUR:".Length..].Trim());
            }
            results.Add(result);
            await Task.Delay(300, cancellationToken);

            // Verify action effect: take a post-action screenshot for significant actions
            if (action.Type is ActionType.OpenApp or ActionType.ClickAt or ActionType.TypeText
                or ActionType.Delete or ActionType.PressKey)
            {
                try
                {
                    var postAction = await _computerUse.ObserveAsync(cancellationToken);
                    if (postAction is not null)
                    {
                        var postHint = Truncate(postAction.OcrText, 150);
                        LogDebug("[CA] Post-action OCR: {O}", postHint);
                        // Append verification context so the model knows what happened
                        results.Add($"[Vérification écran: {postHint}]");
                    }
                }
                catch (Exception ex)
                {
                    LogDebug("[CA] Post-action observe failed: {E}", ex.Message);
                }
            }
        }

        // Capture final screenshot for vision verification
        IReadOnlyList<byte[]>? finalImages = null;
        try
        {
            var finalObs = await _computerUse.ObserveAsync(cancellationToken);
            if (finalObs?.ImagePath is not null && File.Exists(finalObs.ImagePath))
            {
                var pngBytes = await File.ReadAllBytesAsync(finalObs.ImagePath, cancellationToken);
                finalImages = new[] { pngBytes };
                LogDebug("[CA] Final screenshot captured for vision: {W}x{H}", finalObs.ScreenWidth, finalObs.ScreenHeight);
            }
        }
        catch (Exception ex)
        {
            LogDebug("[CA] Final screenshot capture failed: {E}", ex.Message);
        }

        var message = string.Join("\n", results);
        return finalImages is not null ? Ok(message, finalImages) : Ok(message);
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
        if (focused)
        {
            // Verify focus actually worked after a short delay
            await Task.Delay(150, ct);
            var verifyFg = await _controller.GetForegroundWindowAsync(ct);
            if (verifyFg == target.Handle) return null;

            // Focus reported success but window not in foreground — retry once
            await _controller.FocusWindowAsync(target.Handle, ct);
            await Task.Delay(200, ct);
            var retryFg = await _controller.GetForegroundWindowAsync(ct);
            if (retryFg == target.Handle) return null;
        }

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

        // Windows FR ↔ EN aliases
        if (lower is "bloc-notes" or "bloc note" or "notepad" or "éditeur de texte") AddAlias("bloc-notes", "notepad", "éditeur de texte");
        if (lower is "calculatrice" or "calculateur" or "calculator" or "calculette") AddAlias("calculatrice", "calculateur", "calculator", "calculette");
        if (lower is "explorateur" or "explorer" or "explorateur de fichiers" or "ce pc" or "poste de travail") AddAlias("explorateur", "explorer", "ce pc", "poste de travail");
        if (lower is "registre" or "regedit" or "éditeur du registre") AddAlias("registre", "regedit", "éditeur du registre");
        if (lower == "paint" || lower == "peinture" || lower == "mspaint" || lower == "dessin") AddAlias("paint", "peinture", "mspaint", "dessin");
        if (lower is "cmd" or "terminal" or "invite de commandes" or "powershell" or "console")
            AddAlias("cmd", "terminal", "powershell", "invite de commandes", "console");
        if (lower is "word" or "microsoft word" or "ms word") AddAlias("word", "microsoft word");
        if (lower is "excel" or "microsoft excel" or "tableur") AddAlias("excel", "microsoft excel", "tableur");
        if (lower is "chrome" or "google chrome") AddAlias("chrome", "google chrome");
        if (lower is "edge" or "microsoft edge" or "navigateur") AddAlias("edge", "microsoft edge", "navigateur");
        if (lower is "code" or "vs code" or "visual studio code") AddAlias("code", "vs code", "visual studio code");
        if (lower is "gestionnaire de tâches" or "task manager" or "taskmgr") AddAlias("gestionnaire de tâches", "task manager", "taskmgr");

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
        // OPEN APP — handles "ouvre le bloc-notes", "lance blender", "ouvrir paint"
        if (text.Contains("ouvre") || text.Contains("lance") || text.Contains("ouvrir") || text.Contains("lancer"))
        {
            var app = ExtractAfter(text, new[] { "ouvre le ", "ouvre la ", "ouvre ", "lance le ", "lance la ", "lance ", "ouvrir le ", "ouvrir la ", "ouvrir ", "lancer le ", "lancer la ", "lancer " });
            return new PlannedAction(ActionType.OpenApp, AppName: app, Description: $"Ouvrir {app}");
        }

        // DELETE — handles "supprime le cube", "supprimer la caméra", "efface tout"
        if (text.Contains("supprim") || text.Contains("suppression") ||
            text.Contains("effac") || text.Contains("delete") ||
            text.Contains("enlève") || text.Contains("enleve"))
        {
            var target = CleanDeleteTarget(ExtractAfter(text, new[]
                { "supprimer le ", "supprimer la ", "supprimer les ", "supprimer ",
                  "supprime le ", "supprime la ", "supprime les ", "supprime ",
                  "supprim", "efface le ", "efface la ", "efface ", "effacer ", "delete" }));
            return new PlannedAction(ActionType.Delete, Target: target,
                Description: $"Supprimer '{target}'");
        }

        // CLOSE
        if (text.Contains("ferme") || text.Contains("close"))
            return new PlannedAction(ActionType.CloseWindow, Description: "Fermer");

        // CLICK — handles "clique sur le bouton X", "clique sur X", "clic sur X"
        if (text.Contains("clique") || text.Contains("clic") || text.Contains("click"))
        {
            var target = ExtractAfter(text, new[] { "clique sur le bouton ", "clique sur la bouton ", "clique sur ", "clique ",
                "clic sur le bouton ", "clic sur la bouton ", "clic sur ", "clic ", "click on ", "click " });
            var element = FindElement(target, elements);
            if (element is not null)
                return new PlannedAction(ActionType.ClickAt, ClickX: element.CenterX, ClickY: element.CenterY,
                    Description: $"Cliquer sur '{element.Label}' ({element.CenterX},{element.CenterY})");
            return new PlannedAction(ActionType.ClickAt, ClickX: null, ClickY: null,
                Description: $"Cliquer sur '{target}'");
        }

        // FOCUS / "passe dessus" — bring app to foreground
        if (text.Contains("passe dessus") || text.Contains("passe sur") || text.Contains("va sur"))
            return new PlannedAction(ActionType.ClickAt, ClickX: null, ClickY: null,
                Description: "Mettre au premier plan");

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

        // TYPE — handles "tape X", "écris X", "écri X dans le champ"
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

    private async Task<string> ExecuteActionAsync(PlannedAction action, UiObservation? observation,
        IReadOnlyList<UiElement> elements, CancellationToken ct)
    {
        return action.Type switch
        {
            ActionType.OpenApp => await DoOpenApp(action, ct),
            ActionType.CloseWindow => await DoClose(ct),
            ActionType.ClickAt => await DoClickAt(action, ct),
            ActionType.Delete => await DoDelete(ct),
            ActionType.DrawSubject => await DoDrawSubject(action, observation, ct),
            ActionType.FillColor => await DoFillColor(action, observation, ct),
            ActionType.DrawShape => await DoDrawShape(action, observation, ct),
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
            await Task.Delay(200, ct);
            return $"{name} déjà ouvert au premier plan.";
        }

        // 1) Lancement DIRECT si on sait où se trouve l'app (raccourci menu Démarrer,
        //    registre App Paths, exe connu). C'est DÉTERMINISTE, contrairement à la
        //    simulation « Win + taper le nom » qui échoue quand la recherche du shell
        //    n'est pas au bon endroit : le nom est alors tapé dans la mauvaise fenêtre
        //    (bug observé : « blender » écrit dans le champ de saisie du chat).
        if (TryResolveAppTarget(name, out var launchTarget))
        {
            LogDebug("[CA] Lancement direct: {Target}", launchTarget);
            try
            {
                Process.Start(new ProcessStartInfo(launchTarget) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogDebug("[CA] Lancement direct échoué: {Err}", ex.Message);
                return await LaunchViaStartMenuAsync(name, aliases, ct);
            }

            // Les grosses applications (Blender, Photoshop…) mettent 10-20 s à
            // afficher leur fenêtre : on patiente bien plus que les 6 s d'avant.
            var openedDirect = await WaitForAppWindowAsync(name, aliases, ct, attempts: 40);
            if (openedDirect is not null) return openedDirect;

            return $"ERREUR: {name} a été lancé ({Path.GetFileName(launchTarget)}) mais sa fenêtre " +
                   $"n'apparaît pas. Vérifie qu'il s'est bien ouvert, puis relance ta demande.";
        }

        // 2) Repli : lancement « comme un humain » via le menu Démarrer.
        LogDebug("[CA] App non résolue, lancement via menu Démarrer");
        return await LaunchViaStartMenuAsync(name, aliases, ct);
    }

    private async Task<string> LaunchViaStartMenuAsync(string name, IReadOnlyList<string> aliases, CancellationToken ct)
    {
        await _controller.PressKeyAsync("win", ct);
        await Task.Delay(300, ct);
        await _controller.TypeTextAsync(name, ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("enter", ct);

        var opened = await WaitForAppWindowAsync(name, aliases, ct, attempts: 40);
        if (opened is not null) return opened;

        // Fenêtre jamais détectée : ÉCHEC honnête, l'agent doit changer d'approche.
        var observation = await _computerUse.ObserveAsync(ct);
        var screenHint = observation is null ? "écran non observable" : $"Écran : {Truncate(observation.OcrText, 150)}";
        return $"ERREUR: {name} n'a pas pu être ouvert (fenêtre introuvable). {screenHint}";
    }

    /// <summary>
    /// Attend l'apparition de la fenêtre de l'app (polling), la met au premier plan
    /// et renvoie un message de succès. Null si la fenêtre n'apparaît jamais.
    /// </summary>
    private async Task<string?> WaitForAppWindowAsync(
        string name, IReadOnlyList<string> aliases, CancellationToken ct, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            await Task.Delay(300, ct);
            var target = await FindWindowAsync(aliases, ct);
            if (target is null) continue;

            await _controller.FocusWindowAsync(target.Handle, ct);
            await Task.Delay(200, ct);
            var confirmed = await _computerUse.ObserveAsync(ct);
            var hint = confirmed is null ? string.Empty : Truncate(confirmed.OcrText, 120);
            return $"{name} ouvert au premier plan. Écran : {hint}";
        }
        return null;
    }

    /// <summary>
    /// Résout la cible de lancement d'une application, dans l'ordre de fiabilité :
    /// raccourci du menu Démarrer (.lnk), entrée registre « App Paths », puis exe
    /// dans un dossier Program Files dont le nom contient celui de l'app.
    /// </summary>
    private static bool TryResolveAppTarget(string name, out string target)
    {
        target = string.Empty;
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return false;

        // Map French app names to English exe names for Windows EN compatibility
        var exeName = trimmed.ToLowerInvariant() switch
        {
            // Bloc-notes
            "bloc-notes" or "bloc note" or "notepad" or "éditeur de texte" or "bloc-notes windows" => "notepad.exe",
            // Calculatrice
            "calculatrice" or "calculateur" or "calculator" or "calculette" or "calc" => "calc.exe",
            // Explorateur
            "explorateur" or "explorateur de fichiers" or "explorer" or "mes documents" or "ce pc" or "poste de travail" or "ordinateur" => "explorer.exe",
            // Registre
            "registre" or "regedit" or "registre windows" or "éditeur du registre" => "regedit.exe",
            // Paint
            "peinture" or "paint" or "mspaint" or "dessin" => "mspaint.exe",
            // Terminal
            "terminal" or "cmd" or "invite de commandes" or "console" or "powershell" => "cmd.exe",
            // Word
            "word" or "microsoft word" or "ms word" or " traitement de texte" => "winword.exe",
            // Excel
            "excel" or "microsoft excel" or "ms excel" or "tableur" => "excel.exe",
            // Chrome
            "chrome" or "google chrome" => "chrome.exe",
            // Edge
            "edge" or "microsoft edge" or "navigateur" => "msedge.exe",
            // Firefox
            "firefox" or "mozilla" => "firefox.exe",
            // VS Code
            "code" or "vs code" or "visual studio code" or "éditeur code" => "code.exe",
            // Gestionnaire de tâches
            "gestionnaire de tâches" or "gestionnaire tâches" or "task manager" or "taskmgr" => "taskmgr.exe",
            // Panneau de configuration
            "panneau de configuration" or "paramètres" or "settings" or "control" => "control.exe",
            // Paint 3D
            "paint 3d" or "paint3d" => "mspaint.exe",
            // Snipping Tool
            "outil capture" or "capture d'écran" or "snipping tool" => "SnippingTool.exe",
            // Unknown — fallback: if already .exe use as-is, else append .exe
            _ => trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".exe"
        };

        // FAST PATH: known exe mapping → launch directly via Process.Start
        // This avoids unreliable Start Menu shortcut search for FR→EN name mismatches.
        var knownExes = new[] { "notepad.exe", "calc.exe", "explorer.exe", "regedit.exe", "mspaint.exe",
            "cmd.exe", "winword.exe", "excel.exe", "chrome.exe", "msedge.exe", "firefox.exe",
            "code.exe", "taskmgr.exe", "control.exe", "SnippingTool.exe" };
        if (knownExes.Contains(exeName, StringComparer.OrdinalIgnoreCase))
        {
            target = exeName;
            return true;
        }

        // a) Raccourcis du menu Démarrer (le plus fiable : c'est ce que clique l'utilisateur).
        var startMenus = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs")
        };
        foreach (var dir in startMenus)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var lnk = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileNameWithoutExtension(f)
                        .Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length)
                    .FirstOrDefault();
                if (lnk is not null) { target = lnk; return true; }
            }
            catch { }
        }

        // b) Registre « App Paths » (ex: blender.exe, notepad.exe).
        var appPathKeys = new[]
        {
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}",
            $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{exeName}"
        };
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var keyPath in appPathKeys)
            {
                try
                {
                    using var key = hive.OpenSubKey(keyPath);
                    if (key?.GetValue(null) is string path && File.Exists(path))
                    {
                        target = path;
                        return true;
                    }
                }
                catch { }
            }
        }

        // c) Exe direct dans un dossier Program Files dont le nom contient l'app.
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(root))
                {
                    if (!Path.GetFileName(sub).Contains(trimmed, StringComparison.OrdinalIgnoreCase)) continue;

                    var exe = Path.Combine(sub, trimmed + ".exe");
                    if (File.Exists(exe)) { target = exe; return true; }

                    var anyExe = Directory.EnumerateFiles(sub, "*.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                            .Contains(trimmed, StringComparison.OrdinalIgnoreCase));
                    if (anyExe is not null) { target = anyExe; return true; }
                }
            }
            catch { }
        }

        return false;
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
        await Task.Delay(200, ct);
        var confirmOk = await _controller.PressKeyAsync("enter", ct);
        await Task.Delay(200, ct);

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
        // No coordinates: focus the foreground window ("passe dessus" = bring to focus)
        var fgHandle = await _controller.GetForegroundWindowAsync(ct);
        if (fgHandle != 0)
        {
            var ok = await _controller.FocusWindowAsync(fgHandle, ct);
            return ok ? "Fenêtre mise au premier plan." : "Échec du focus.";
        }
        return "Pas de fenêtre active.";
    }

    private async Task<string> DoFillColor(PlannedAction a, UiObservation? obs, CancellationToken ct)
    {
        var color = a.Color ?? "000000";
        LogDebug("[CA] FillColor: paint-specific shortcuts used (g + ctrl+l). Generic fallback not available.");
        await _controller.PressKeyAsync("g", ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("ctrl+l", ct);
        await Task.Delay(300, ct);
        await _controller.TypeTextAsync(color, ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("enter", ct);
        await Task.Delay(200, ct);
        var w = obs?.ScreenWidth ?? 1920; var h = obs?.ScreenHeight ?? 1080;
        await _controller.ClickAsync(MouseButton.Left, w / 2, h / 2, ct);
        return $"Rempli #{color} (Paint-specific).";
    }

    private async Task<string> DoDrawShape(PlannedAction a, UiObservation? obs, CancellationToken ct)
    {
        var color = a.Color ?? "000000";
        var shape = a.Shape ?? "cercle";
        LogDebug("[CA] DrawShape: paint-specific shortcuts used (o + ctrl+l). Generic fallback not available.");
        await _controller.PressKeyAsync("o", ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("ctrl+l", ct);
        await Task.Delay(300, ct);
        await _controller.TypeTextAsync(color, ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("enter", ct);
        await Task.Delay(200, ct);
        var w = obs?.ScreenWidth ?? 1920; var h = obs?.ScreenHeight ?? 1080;
        var cx = w / 2; var cy = h / 2;
        var s = Math.Min(w, h) / 4;
        await _controller.DragAsync(cx - s, cy - s, cx + s, cy + s, MouseButton.Left, ct);
        return $"{shape} #{color} (Paint-specific).";
    }

    private async Task<string> DoDrawSubject(PlannedAction a, UiObservation? obs, CancellationToken ct)
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
        await Task.Delay(200, ct);

        var subject = (a.Shape ?? "").ToLowerInvariant();
        var w = obs?.ScreenWidth ?? 1920; var h = obs?.ScreenHeight ?? 1080;
        var cx = w / 2; var cy = h / 2;
        var s = Math.Min(w, h) / 5;

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
        var raw = m.Success ? m.Groups[1].Value : text.Trim();
        // Unescape literal escape sequences so \n → real newline, \t → tab, etc.
        return raw
            .Replace("\\n", "\n")
            .Replace("\\t", "\t")
            .Replace("\\r", "\r")
            .Replace("\\\\", "\\");
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