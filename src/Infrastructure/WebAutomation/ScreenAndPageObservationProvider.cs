using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Vision;
using JarvisAI.Application.WebAutomation;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.WebAutomation;

public sealed class ScreenAndPageObservationProvider : IObservationProvider
{
    private readonly IComputerController? _computer;
    private readonly IOcrService? _ocr;
    private readonly IWebBrowser? _browser;
    private readonly IComputerUseService? _computerUse;
    private readonly ILogger<ScreenAndPageObservationProvider> _logger;

    public ScreenAndPageObservationProvider(
        IComputerController? computer,
        IOcrService? ocr,
        IWebBrowser? browser,
        ILogger<ScreenAndPageObservationProvider> logger,
        IComputerUseService? computerUse = null)
    {
        _computer = computer;
        _ocr = ocr;
        _browser = browser;
        _computerUse = computerUse;
        _logger = logger;
    }

    public async Task<string> ObserveAsync(string goal, CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();

        if (_computerUse is not null)
        {
            parts.AddRange(await ObserveScreenViaComputerUseAsync(cancellationToken));
        }
        else if (_computer is not null && _ocr is not null)
        {
            parts.AddRange(await ObserveScreenLegacyAsync(cancellationToken));
        }

        if (_browser is not null)
        {
            try
            {
                var pageText = await _browser.GetTextAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    var url = await _browser.GetUrlAsync(cancellationToken) ?? "?";
                    parts.Add($"BROWSER PAGE ({url}):\n{Truncate(pageText, 3000)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ObservationProvider] Browser read failed");
            }
        }

        return parts.Count == 0 ? string.Empty : string.Join("\n\n---\n\n", parts);
    }

    private async Task<IReadOnlyList<string>> ObserveScreenViaComputerUseAsync(CancellationToken cancellationToken)
    {
        var parts = new List<string>();

        try
        {
            var observation = await _computerUse!.ObserveAsync(cancellationToken);
            if (observation is null)
                return parts;

            parts.Add($"SCREEN: {observation.ScreenWidth}x{observation.ScreenHeight}, cursor at ({observation.CursorX}, {observation.CursorY})");

            if (!string.IsNullOrWhiteSpace(observation.OcrText))
                parts.Add($"SCREEN OCR:\n{Truncate(observation.OcrText, 3000)}");

            if (observation.Elements.Count > 0)
            {
                var elementLines = observation.Elements
                    .Select(e => $"  #{e.Id} {e.Type.ToString().ToLowerInvariant()} \"{e.Label}\" at ({e.CenterX}, {e.CenterY}) [{e.Width}x{e.Height}, conf {e.Confidence:F2}]");
                parts.Add($"UI ELEMENTS ({observation.Elements.Count}):\n{string.Join("\n", elementLines)}");
            }

            if (observation.Windows.Count > 0)
            {
                var windowLines = observation.Windows
                    .Select(w => $"  {(w.IsFocused ? "* " : "  ")}{w.Title} (handle {w.Handle}) at ({w.X},{w.Y}) {w.Width}x{w.Height}");
                parts.Add($"WINDOWS ({observation.Windows.Count}):\n{string.Join("\n", windowLines)}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ObservationProvider] Computer use observation failed");
        }

        return parts;
    }

    private async Task<IReadOnlyList<string>> ObserveScreenLegacyAsync(CancellationToken cancellationToken)
    {
        var parts = new List<string>();

        try
        {
            var shot = await _computer!.CaptureScreenAsync(cancellationToken);
            if (shot is not null)
            {
                parts.Add($"SCREEN: {shot.Width}x{shot.Height}, cursor at ({shot.CursorX}, {shot.CursorY})");
                var ocr = await _ocr!.ExtractTextAsync(shot.PngBytes, cancellationToken: cancellationToken);
                if (ocr is not null && !string.IsNullOrWhiteSpace(ocr.Text))
                    parts.Add($"SCREEN OCR:\n{Truncate(ocr.Text, 3000)}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ObservationProvider] Screen capture failed");
        }

        return parts;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
