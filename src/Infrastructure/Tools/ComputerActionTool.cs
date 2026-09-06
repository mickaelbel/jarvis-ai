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

    public override string Name => "computer_action";
    public override string Description => "Exécute n'importe quelle action sur l'ordinateur. Capture écran + OCR + recherche web si besoin. UN SEUL APPEL suffit.";
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
        IWebSearchService? webSearch = null)
        : base(logger)
    {
        _controller = controller;
        _computerUse = computerUse;
        _logger = logger;
        _webSearch = webSearch;
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

        // ── Step 7: Execute ────────────────────────────────────────────────
        var results = new List<string>();
        foreach (var action in actions)
        {
            LogDebug("[CA] → {D}", action.Description);
            var result = await ExecuteActionAsync(action, capture, elements, cancellationToken);
            results.Add(result);
            await Task.Delay(500, cancellationToken);
        }

        return results.Count > 0
            ? Ok(string.Join("\n", results))
            : Ok("Action exécutée.");
    }

    // ── App Detection (scans ENTIRE instruction) ──────────────────────────

    private static string? DetectApp(string instruction)
    {
        var lower = instruction.ToLowerInvariant();
        var apps = new[] { "paint", "photoshop", "gimp", "blender", "excel", "word",
                           "notepad", "bloc-notes", "calculator", "chrome", "firefox", "edge" };
        foreach (var app in apps)
        {
            if (lower.Contains(app)) return app;
        }
        return null;
    }

    private async Task<bool> FocusAppWindowAsync(string app, CancellationToken ct)
    {
        var windows = await _controller.ListWindowsAsync(ct);
        var target = windows.FirstOrDefault(w =>
            w.Title.Contains(app, StringComparison.OrdinalIgnoreCase));
        if (target is null) return false;
        return await _controller.FocusWindowAsync(target.Handle, ct);
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

        // FILL COLOR
        if (text.Contains("rempli") || text.Contains("remplir") || text.Contains("fond") || text.Contains("colori"))
        {
            var color = ExtractColor(text);
            return new PlannedAction(ActionType.FillColor, Color: color, Description: $"Remplir #{color}");
        }

        // DRAW SHAPE
        if (text.Contains("dessin") || text.Contains("cercle") || text.Contains("carré") ||
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
        await _controller.PressKeyAsync("win", ct);
        await Task.Delay(500, ct);
        await _controller.TypeTextAsync(name, ct);
        await Task.Delay(300, ct);
        await _controller.PressKeyAsync("enter", ct);
        await Task.Delay(1500, ct);
        return $"{name} lancé.";
    }

    private async Task<string> DoClose(CancellationToken ct)
    {
        var h = await _controller.GetForegroundWindowAsync(ct);
        return h != 0 && await _controller.CloseWindowAsync(h, ct) ? "Fermé." : "Échec.";
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
        await _controller.PressKeyAsync("g", ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("ctrl+l", ct);
        await Task.Delay(500, ct);
        await _controller.TypeTextAsync(color, ct);
        await Task.Delay(200, ct);
        await _controller.PressKeyAsync("enter", ct);
        await Task.Delay(300, ct);
        await _controller.ClickAsync(MouseButton.Left, cap.Width / 2, cap.Height / 2, ct);
        return $"Rempli #{color}.";
    }

    private async Task<string> DoDrawShape(PlannedAction a, ScreenCapture cap, CancellationToken ct)
    {
        var color = a.Color ?? "000000";
        var shape = a.Shape ?? "cercle";
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
        return $"{shape} #{color}.";
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
        OpenApp, CloseWindow, ClickAt, FillColor, DrawShape, TypeText, PressKey, Scroll
    }

    private sealed record PlannedAction(
        ActionType Type,
        string? AppName = null, string? Color = null, string? Shape = null,
        string? Target = null, string? Key = null, string? Text = null,
        string? ScrollDir = null, int? ClickX = null, int? ClickY = null,
        string Description = "");
}
