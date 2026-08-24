using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Computer use execution: observes the screen, finds UI elements by label and
/// performs intelligent clicks / typing. High risk because it drives the real
/// mouse and keyboard.
/// </summary>
public sealed class ComputerUseTool : ITool
{
    private readonly IComputerUseService _computerUse;
    private readonly ILogger<ComputerUseTool> _logger;

    public string Name => "computer_use";
    public string Description => "Operate the computer by observing the screen and acting on detected UI elements by label. Actions: observe, find_element, click_element, double_click_element, type_into";
    public string Category => "computer_use";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public string? WaitingPhrase => "Je manipule ton écran.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: observe, find_element, click_element, double_click_element, type_into", typeof(string), required: true),
        new ToolParameter("label", "Text label of the element to act on, e.g. 'OK' or 'Champ email'", typeof(string)),
        new ToolParameter("text", "Text to type (for type_into)", typeof(string)),
        new ToolParameter("button", "Mouse button: left, right, middle (for click_element)", typeof(string))
    };

    public ComputerUseTool(IComputerUseService computerUse, ILogger<ComputerUseTool> logger)
    {
        _computerUse = computerUse;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("label", out var label);
        parameters.TryGetValue("text", out var text);
        parameters.TryGetValue("button", out var button);

        try
        {
            return action?.ToLowerInvariant() switch
            {
                "observe" => await ObserveAsync(cancellationToken),
                "find_element" => await FindElementAsync(label, cancellationToken),
                "click_element" => await ClickElementAsync(label, button, cancellationToken),
                "double_click_element" => await DoubleClickElementAsync(label, button, cancellationToken),
                "type_into" => await TypeIntoAsync(label, text, cancellationToken),
                _ => ToolResult.Failed($"Unknown action: {action}. Valid: observe, find_element, click_element, double_click_element, type_into")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ComputerUseTool] Action {Action} failed", action);
            return ToolResult.Failed($"Computer use error: {ex.Message}");
        }
    }

    private async Task<ToolResult> ObserveAsync(CancellationToken cancellationToken)
    {
        if (!_computerUse.IsAvailable)
            return ToolResult.Failed("Computer control is only available on Windows");

        var observation = await _computerUse.ObserveAsync(cancellationToken);
        if (observation is null)
            return ToolResult.Failed("Failed to capture the screen");

        var payload = new
        {
            screen = new { observation.ScreenWidth, observation.ScreenHeight, observation.CursorX, observation.CursorY },
            ocr_text = Truncate(observation.OcrText, 4000),
            elements = observation.Elements.Select(ToElementPayload),
            windows = observation.Windows.Select(w => new { w.Handle, w.Title, w.IsFocused, w.X, w.Y, w.Width, w.Height }),
            image_path = observation.ImagePath
        };

        _logger.LogInformation("[ComputerUseTool] Observed screen: {Elements} elements, {Windows} windows", observation.Elements.Count, observation.Windows.Count);
        return ToolResult.Succeeded(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<ToolResult> FindElementAsync(string? label, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label))
            return ToolResult.Failed("Parameter 'label' is required (e.g. 'OK' or 'Envoyer')");

        var element = await _computerUse.FindElementAsync(label, cancellationToken);
        return element is null
            ? ToolResult.Failed($"No UI element found matching '{label}'. Run 'observe' first to list visible elements.")
            : ToolResult.Succeeded(JsonSerializer.Serialize(ToElementPayload(element), new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<ToolResult> ClickElementAsync(string? label, string? button, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label))
            return ToolResult.Failed("Parameter 'label' is required (e.g. 'OK' or 'Envoyer')");

        var parsedButton = ParseButton(button);
        var result = await _computerUse.ClickElementAsync(label, parsedButton, cancellationToken);
        return result.Success
            ? ToolResult.Succeeded(result.Message)
            : ToolResult.Failed(result.Message);
    }

    private async Task<ToolResult> DoubleClickElementAsync(string? label, string? button, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label))
            return ToolResult.Failed("Parameter 'label' is required (e.g. 'OK' or 'Envoyer')");

        var parsedButton = ParseButton(button);
        var result = await _computerUse.DoubleClickElementAsync(label, parsedButton, cancellationToken);
        return result.Success
            ? ToolResult.Succeeded(result.Message)
            : ToolResult.Failed(result.Message);
    }

    private async Task<ToolResult> TypeIntoAsync(string? label, string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label))
            return ToolResult.Failed("Parameter 'label' is required (e.g. 'input_Nom')");

        if (text is null)
            return ToolResult.Failed("Parameter 'text' is required");

        var result = await _computerUse.TypeIntoElementAsync(label, text, cancellationToken);
        return result.Success
            ? ToolResult.Succeeded(result.Message)
            : ToolResult.Failed(result.Message);
    }

    private static object ToElementPayload(UiElement e) => new
    {
        id = e.Id,
        type = e.Type.ToString().ToLowerInvariant(),
        label = e.Label,
        x = e.X,
        y = e.Y,
        width = e.Width,
        height = e.Height,
        centerX = e.CenterX,
        centerY = e.CenterY,
        confidence = e.Confidence
    };

    private static MouseButton ParseButton(string? button)
    {
        return button?.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left
        };
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength] + "...";
}
