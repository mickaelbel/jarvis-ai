using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// High-level Paint operations: fill canvas, select color, open, save, etc.
/// Eliminates the need for the agent to figure out click sequences.
/// </summary>
public sealed class PaintTool : ToolBase
{
    private readonly IComputerController _controller;
    private readonly ILogger<PaintTool> _logger;

    public override string Name => "paint";
    public override string Description => "Contrôle Paint (MS Paint). Actions: fill_canvas (remplir tout le canvas d'une couleur), open_paint (ouvrir Paint), save (Ctrl+S), select_all (Ctrl+A), set_foreground_color (définir la couleur de premier plan via raccourci clavier).";
    public override string Category => "computer_use";
    public override SecurityRiskLevel RiskLevel => SecurityRiskLevel.Medium;
    public override string? WaitingPhrase => "Je manipule Paint.";

    public override IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "fill_canvas, open_paint, save, select_all, set_foreground_color", typeof(string), required: true),
        new ToolParameter("color", "Hex color like '000000' (black), 'FFFFFF' (white), 'FF0000' (red). For fill_canvas and set_foreground_color.", typeof(string))
    };

    public PaintTool(IComputerController controller, ILogger<PaintTool> logger)
        : base(logger)
    {
        _controller = controller;
        _logger = logger;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(
        AgentContext context,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("color", out var color);

        return action?.ToLowerInvariant() switch
        {
            "fill_canvas" => await FillCanvasAsync(color, cancellationToken),
            "open_paint" => await OpenPaintAsync(cancellationToken),
            "save" => await SaveAsync(cancellationToken),
            "select_all" => await SelectAllAsync(cancellationToken),
            "set_foreground_color" => await SetForegroundColorAsync(color, cancellationToken),
            _ => Fail($"Action inconnue : '{action}'. Actions valides : fill_canvas, open_paint, save, select_all, set_foreground_color")
        };
    }

    private async Task<ToolResult> FillCanvasAsync(string? color, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(color))
            color = "000000";

        _logger.LogInformation("[PaintTool] Filling canvas with color #{Color}", color);

        // Step 1: Ensure Paint is in focus
        var focused = await FocusPaintAsync(cancellationToken);
        if (!focused)
            return Fail("Paint n'est pas ouvert ou n'a pas pu être focus.");

        await Task.Delay(200, cancellationToken);

        // Step 2: Ctrl+A to select all
        await _controller.PressKeyAsync("ctrl+a", cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Step 3: Set the color via Edit > Fill or keyboard shortcut
        // In MS Paint, we can use the fill bucket tool:
        // 1. Press 'G' to select the Fill tool (bucket)
        // 2. Set the foreground color
        // 3. Click on the canvas

        // First, set the foreground color using Edit > Colors or Ctrl+L for color picker
        // But simpler: use the keyboard shortcut to open color dialog
        // Actually, the most reliable way is to:
        // 1. Press 'G' for fill tool
        // 2. Set color via Ctrl+E (Edit Colors)
        // 3. Click

        // Let's use a different approach: Select All + Delete fills with background color
        // Or: Use Ctrl+A then set background color and press Delete

        // Most reliable: Use the Fill tool (press 'G' in Paint)
        await _controller.PressKeyAsync("g", cancellationToken);
        await Task.Delay(300, cancellationToken);

        // Set the foreground color using the color picker
        // Press Ctrl+L to open the "Edit Colors" dialog
        await _controller.PressKeyAsync("ctrl+l", cancellationToken);
        await Task.Delay(500, cancellationToken);

        // Type the hex color in the "Edit Colors" dialog
        // The hex field is usually focused. Type the color.
        await _controller.TypeTextAsync(color, cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Press Enter to confirm
        await _controller.PressKeyAsync("enter", cancellationToken);
        await Task.Delay(300, cancellationToken);

        // Click on the canvas to fill
        // Get screen center for click
        var capture = await _controller.CaptureScreenAsync(cancellationToken);
        if (capture is not null)
        {
            var centerX = capture.Width / 2;
            var centerY = capture.Height / 2;
            await _controller.ClickAsync(MouseButton.Left, centerX, centerY, cancellationToken);
            await Task.Delay(200, cancellationToken);
        }

        _logger.LogInformation("[PaintTool] Canvas filled with color #{Color}", color);
        return Ok($"Canvas rempli avec la couleur #{color}.");
    }

    private async Task<ToolResult> OpenPaintAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[PaintTool] Opening Paint");

        // Use Run dialog or Start menu to open Paint
        await _controller.PressKeyAsync("win", cancellationToken);
        await Task.Delay(500, cancellationToken);
        await _controller.TypeTextAsync("paint", cancellationToken);
        await Task.Delay(300, cancellationToken);
        await _controller.PressKeyAsync("enter", cancellationToken);
        await Task.Delay(1000, cancellationToken);

        return Ok("Paint ouvert.");
    }

    private async Task<ToolResult> SaveAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[PaintTool] Saving");
        await FocusPaintAsync(cancellationToken);
        await Task.Delay(200, cancellationToken);
        await _controller.PressKeyAsync("ctrl+s", cancellationToken);
        await Task.Delay(500, cancellationToken);
        return Ok("Sauvegarde lancée.");
    }

    private async Task<ToolResult> SelectAllAsync(CancellationToken cancellationToken)
    {
        await FocusPaintAsync(cancellationToken);
        await Task.Delay(200, cancellationToken);
        await _controller.PressKeyAsync("ctrl+a", cancellationToken);
        return Ok("Tout sélectionné.");
    }

    private async Task<ToolResult> SetForegroundColorAsync(string? color, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(color))
            return Fail("Le paramètre 'color' est requis (ex: '000000' pour noir).");

        await FocusPaintAsync(cancellationToken);
        await Task.Delay(200, cancellationToken);

        // Open color picker
        await _controller.PressKeyAsync("ctrl+l", cancellationToken);
        await Task.Delay(500, cancellationToken);

        // Type hex color
        await _controller.TypeTextAsync(color, cancellationToken);
        await Task.Delay(200, cancellationToken);

        await _controller.PressKeyAsync("enter", cancellationToken);
        await Task.Delay(200, cancellationToken);

        return Ok($"Couleur de premier plan définie : #{color}.");
    }

    private async Task<bool> FocusPaintAsync(CancellationToken cancellationToken)
    {
        var windows = await _controller.ListWindowsAsync(cancellationToken);
        var paintWindow = windows.FirstOrDefault(w =>
            w.Title.Contains("Paint", StringComparison.OrdinalIgnoreCase) ||
            w.Title.Contains("mspaint", StringComparison.OrdinalIgnoreCase));

        if (paintWindow is null)
            return false;

        return await _controller.FocusWindowAsync(paintWindow.Handle, cancellationToken);
    }
}
