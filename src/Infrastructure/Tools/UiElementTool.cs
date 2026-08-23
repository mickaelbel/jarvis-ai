using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Read-only UI inspection: detects interactive elements on the screen (buttons,
/// links, dropdowns, inputs...) and matches them by label. Never clicks or types.
/// </summary>
public sealed class UiElementTool : ITool
{
    private readonly IComputerUseService _computerUse;
    private readonly ILogger<UiElementTool> _logger;

    public string Name => "ui_elements";
    public string Description => "Detect UI elements on the screen (buttons, links, dropdowns, inputs) and match them by label. Read-only. Actions: detect, find";
    public string Category => "computer_use";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("action", "Operation: detect (list all elements), find (match element by label)", typeof(string), required: true),
        new ToolParameter("label", "Text label to search for (for find), e.g. 'OK' or 'Envoyer'", typeof(string))
    };

    public UiElementTool(IComputerUseService computerUse, ILogger<UiElementTool> logger)
    {
        _computerUse = computerUse;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("label", out var label);

        try
        {
            return action?.ToLowerInvariant() switch
            {
                "detect" => await DetectAsync(cancellationToken),
                "find" => await FindAsync(label, cancellationToken),
                _ => ToolResult.Failed($"Unknown action: {action}. Valid: detect, find")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UiElementTool] Action {Action} failed", action);
            return ToolResult.Failed($"UI element detection error: {ex.Message}");
        }
    }

    private async Task<ToolResult> DetectAsync(CancellationToken cancellationToken)
    {
        if (!_computerUse.IsAvailable)
            return ToolResult.Failed("Computer control is only available on Windows");

        var observation = await _computerUse.ObserveAsync(cancellationToken);
        if (observation is null)
            return ToolResult.Failed("Failed to capture the screen");

        var payload = new
        {
            screen = new { observation.ScreenWidth, observation.ScreenHeight, observation.CursorX, observation.CursorY },
            count = observation.Elements.Count,
            elements = observation.Elements.Select(ToPayload)
        };

        _logger.LogInformation("[UiElementTool] Detected {Count} UI elements", observation.Elements.Count);
        return ToolResult.Succeeded(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<ToolResult> FindAsync(string? label, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label))
            return ToolResult.Failed("Parameter 'label' is required (e.g. 'OK' or 'Envoyer')");

        var element = await _computerUse.FindElementAsync(label, cancellationToken);

        if (element is null)
        {
            var notFound = new
            {
                found = false,
                label,
                element = (object?)null,
                hint = "Try running 'detect' to list all visible elements."
            };
            return ToolResult.Succeeded(JsonSerializer.Serialize(notFound, new JsonSerializerOptions { WriteIndented = true }));
        }

        var found = new
        {
            found = true,
            label,
            element = (object?)ToPayload(element),
            hint = (string?)null
        };
        return ToolResult.Succeeded(JsonSerializer.Serialize(found, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object ToPayload(UiElement e) => new
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
}
