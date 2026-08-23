using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.ComputerUse;

/// <summary>
/// Detects interactive UI elements from a screen capture by grouping OCR words
/// into lines, classifying each line by heuristics (buttons, links, dropdowns,
/// checkboxes, labels with adjacent inputs) and producing clickable elements
/// with bounding boxes and confidence scores.
/// </summary>
public sealed class OcrUiElementDetector : IUiElementDetector
{
    private readonly IOcrService _ocr;
    private readonly ILogger<OcrUiElementDetector> _logger;

    public OcrUiElementDetector(IOcrService ocr, ILogger<OcrUiElementDetector> logger)
    {
        _ocr = ocr;
        _logger = logger;
    }

    public bool IsAvailable => _ocr.OcrAvailable;

    public async Task<IReadOnlyList<UiElement>> DetectAsync(byte[] imageBytes, int screenWidth, int screenHeight, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || imageBytes is null || imageBytes.Length == 0)
            return Array.Empty<UiElement>();

        var ocr = await _ocr.ExtractTextAsync(imageBytes, cancellationToken: cancellationToken);
        if (ocr is null || ocr.Words.Count == 0)
            return Array.Empty<UiElement>();

        var lines = GroupIntoLines(ocr.Words);
        var elements = new List<UiElement>();
        var id = 1;

        foreach (var line in lines)
        {
            var element = Classify(line, screenWidth, screenHeight, id++);
            elements.Add(element);

            // A label ending with ':' usually precedes an empty input field.
            // OCR finds no text inside it, so infer an input to the right of the label.
            if (element.Type == UiElementType.Text && element.Label.TrimEnd().EndsWith(':'))
            {
                var input = InferInputAfterLabel(line, element, screenWidth, screenHeight, id++);
                if (input is not null)
                    elements.Add(input);
            }
        }

        _logger.LogInformation("[UiElementDetector] Detected {Count} elements from {WordCount} OCR words", elements.Count, ocr.Words.Count);
        return elements;
    }

    private static IReadOnlyList<Line> GroupIntoLines(IReadOnlyList<OcrWord> words)
    {
        var averageHeight = words.Count == 0
            ? 20
            : words.Average(w => Math.Max(1, w.Height));
        var tolerance = Math.Max(8, averageHeight * 0.5);

        var lines = new List<Line>();
        foreach (var word in words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var wordCenter = word.Y + word.Height / 2.0;
            Line? target = null;
            var bestDistance = double.MaxValue;

            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                var distance = Math.Abs(line.CenterY - wordCenter);
                if (distance <= tolerance && distance < bestDistance)
                {
                    bestDistance = distance;
                    target = line;
                }
            }

            if (target is null)
            {
                lines.Add(new Line(word));
            }
            else
            {
                target.Add(word);
            }
        }

        return SplitLinesByGap(lines)
            .OrderBy(l => l.Y)
            .ThenBy(l => l.X)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Splits visually-merged rows (e.g. a menu bar "Fichier Modifier Affichage")
    /// into separate elements whenever the horizontal gap between two words is
    /// clearly larger than the spacing inside a single label.
    /// </summary>
    private static IEnumerable<Line> SplitLinesByGap(IEnumerable<Line> lines)
    {
        foreach (var line in lines)
        {
            var words = line.Words.OrderBy(w => w.X).ToList();
            if (words.Count < 2)
            {
                yield return line;
                continue;
            }

            var averageWidth = words.Average(w => Math.Max(1, w.Width));
            var threshold = Math.Max(24, averageWidth * 0.8);

            var group = new List<OcrWord> { words[0] };
            for (var i = 1; i < words.Count; i++)
            {
                var gap = words[i].X - (words[i - 1].X + words[i - 1].Width);
                if (gap > threshold)
                {
                    yield return new Line(group);
                    group.Clear();
                }

                group.Add(words[i]);
            }

            yield return new Line(group);
        }
    }

    private static UiElement Classify(Line line, int screenWidth, int screenHeight, int id)
    {
        var label = line.Text;
        var x = Clamp(line.X, 0, screenWidth);
        var y = Clamp(line.Y, 0, screenHeight);
        var width = Clamp(line.Width, 0, screenWidth - x);
        var height = Clamp(line.Height, 0, screenHeight - y);
        var centerX = x + width / 2;
        var centerY = y + height / 2;

        var type = UiElementType.Text;
        var confidence = 0.5;
        var isColonLabel = label.TrimEnd().EndsWith(':');

        if (ContainsCheckboxMarker(label))
        {
            type = UiElementType.Checkbox;
            confidence = 0.75;
        }
        else if (IsDropdown(label))
        {
            type = UiElementType.Dropdown;
            confidence = 0.75;
        }
        else if (IsLink(label))
        {
            type = UiElementType.Link;
            confidence = 0.75;
        }
        else if (isColonLabel)
        {
            type = UiElementType.Text;
            confidence = 0.6;
        }
        else if (IsButton(label))
        {
            type = UiElementType.Button;
            confidence = 0.7;
        }

        if (label.Length > 40)
            confidence -= 0.15;

        return new UiElement(
            id,
            type,
            label,
            x,
            y,
            width,
            height,
            centerX,
            centerY,
            Math.Round(Math.Clamp(confidence, 0.0, 1.0), 2));
    }

    private static UiElement? InferInputAfterLabel(Line labelLine, UiElement labelElement, int screenWidth, int screenHeight, int id)
    {
        var x = labelLine.Right + 8;
        if (x >= screenWidth)
            return null;

        var y = Math.Max(0, labelElement.Y - 4);
        var width = Math.Clamp(screenWidth - x - 8, 60, 600);
        var height = Math.Max(24, labelElement.Height + 8);

        if (y + height > screenHeight)
            height = Math.Max(8, screenHeight - y);

        var inputLabel = labelElement.Label.TrimEnd().TrimEnd(':').Trim();
        if (inputLabel.Length == 0)
            inputLabel = "input";

        return new UiElement(
            id,
            UiElementType.Input,
            $"input_{inputLabel}",
            x,
            y,
            width,
            height,
            x + width / 2,
            y + height / 2,
            0.4);
    }

    private static bool IsButton(string text)
    {
        var trimmed = text.Trim();
        var wordCount = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        if (wordCount > 4 || trimmed.Length > 30)
            return false;

        // Short label, starts with an uppercase letter: typical button caption.
        if (trimmed.Length <= 12 && char.IsUpper(trimmed[0]))
            return true;

        // Fully uppercase (e.g. "OK", "ENVOYER").
        if (trimmed.All(c => !char.IsLetter(c) || char.IsUpper(c)) && trimmed.Length >= 2)
            return true;

        return false;
    }

    private static bool IsLink(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return true;

        return LinkRegex.IsMatch(trimmed);
    }

    private static bool IsDropdown(string text)
        => text.Contains('▾') || text.Contains('▼') || text.Contains('⌄') || text.EndsWith("...");

    private static bool ContainsCheckboxMarker(string text)
        => text.Contains("☑") || text.Contains("☐") || text.Contains("☒") ||
           text.Contains("[x]", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("[ ]", StringComparison.OrdinalIgnoreCase);

    private static int Clamp(int value, int min, int max)
        => Math.Clamp(value, min, max);

    private static readonly Regex LinkRegex = new(
        @"(?:^|\s)[a-z0-9-]+(\.[a-z0-9-]+)+[^\s]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class Line
    {
        private readonly List<OcrWord> _words = new();

        public Line(OcrWord first) => Add(first);

        public Line(IEnumerable<OcrWord> words) => _words.AddRange(words);

        public IReadOnlyList<OcrWord> Words => _words;

        public string Text => string.Join(" ", _words.Select(w => w.Text.Trim()));
        public int X => _words.Min(w => w.X);
        public int Y => _words.Min(w => w.Y);
        public int Right => _words.Max(w => w.X + w.Width);
        public int Bottom => _words.Max(w => w.Y + w.Height);
        public int Width => Right - X;
        public int Height => Bottom - Y;
        public double CenterY => Y + Height / 2.0;

        public void Add(OcrWord word) => _words.Add(word);
    }
}
