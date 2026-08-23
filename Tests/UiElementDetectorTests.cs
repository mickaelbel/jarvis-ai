using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Vision;
using JarvisAI.Infrastructure.ComputerUse;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class UiElementDetectorTests
{
    private readonly FakeOcr _ocr;
    private readonly OcrUiElementDetector _detector;

    public UiElementDetectorTests()
    {
        _ocr = new FakeOcr();
        _detector = new OcrUiElementDetector(_ocr, NullLogger<OcrUiElementDetector>.Instance);
    }

    private Task<IReadOnlyList<UiElement>> DetectAsync(params OcrWord[] words)
    {
        _ocr.Result = new OcrResult(string.Join(" ", words.Select(w => w.Text)), words);
        return _detector.DetectAsync(new byte[] { 1 }, 1920, 1080);
    }

    [Fact]
    public async Task Detects_button_from_short_uppercase_word()
    {
        var elements = await DetectAsync(new OcrWord("OK", 100, 200, 40, 20));

        var button = Assert.Single(elements);
        Assert.Equal(UiElementType.Button, button.Type);
        Assert.Equal("OK", button.Label);
        Assert.Equal(120, button.CenterX);
        Assert.Equal(210, button.CenterY);
        Assert.InRange(button.Confidence, 0.0, 1.0);
    }

    [Fact]
    public async Task Groups_words_on_same_line_into_single_element()
    {
        var elements = await DetectAsync(
            new OcrWord("Send", 50, 50, 40, 18),
            new OcrWord("message", 95, 50, 60, 18));

        var element = Assert.Single(elements);
        Assert.Equal("Send message", element.Label);
        Assert.Equal(50, element.X);
        Assert.Equal(50, element.Y);
        Assert.Equal(105, element.Width);
    }

    [Fact]
    public async Task Label_ending_with_colon_produces_inferred_input()
    {
        var elements = await DetectAsync(new OcrWord("Nom:", 20, 100, 45, 20));

        Assert.Equal(2, elements.Count);
        Assert.Equal(UiElementType.Text, elements[0].Type);
        Assert.Equal("Nom:", elements[0].Label);
        Assert.Equal(UiElementType.Input, elements[1].Type);
        Assert.Equal("input_Nom", elements[1].Label);
        Assert.Equal(73, elements[1].X);
    }

    [Fact]
    public async Task Detects_link_from_url()
    {
        var elements = await DetectAsync(new OcrWord("https://example.com", 10, 30, 150, 18));

        var link = Assert.Single(elements);
        Assert.Equal(UiElementType.Link, link.Type);
    }

    [Fact]
    public async Task Detects_dropdown_marker()
    {
        var elements = await DetectAsync(
            new OcrWord("Langue", 10, 30, 60, 20),
            new OcrWord("▾", 72, 30, 12, 20));

        Assert.Contains(elements, e => e.Type == UiElementType.Dropdown);
    }

    [Fact]
    public async Task Detects_checkbox_marker()
    {
        var elements = await DetectAsync(
            new OcrWord("[x]", 10, 30, 25, 18),
            new OcrWord("Accepter", 38, 30, 70, 18));

        Assert.Contains(elements, e => e.Type == UiElementType.Checkbox);
    }

    [Fact]
    public async Task Long_text_is_classified_as_text_not_button()
    {
        var elements = await DetectAsync(
            new OcrWord("Ceci est un long paragraphe de texte d'information affiché à l'écran", 10, 30, 600, 20));

        var element = Assert.Single(elements);
        Assert.Equal(UiElementType.Text, element.Type);
    }

    [Fact]
    public async Task Empty_ocr_words_returns_no_elements()
    {
        var elements = await DetectAsync();

        Assert.Empty(elements);
    }

    [Fact]
    public async Task Unavailable_ocr_returns_no_elements()
    {
        _ocr.Available = false;
        _ocr.Result = new OcrResult("OK", new[] { new OcrWord("OK", 0, 0, 10, 10) });

        var elements = await _detector.DetectAsync(new byte[] { 1 }, 1920, 1080);

        Assert.Empty(elements);
    }

    [Fact]
    public async Task Coordinates_are_clamped_to_screen_bounds()
    {
        var elements = await DetectAsync(new OcrWord("OK", -50, -20, 40, 20));

        var element = Assert.Single(elements);
        Assert.True(element.X >= 0);
        Assert.True(element.Y >= 0);
    }

    [Fact]
    public async Task Words_on_separate_lines_become_separate_elements()
    {
        var elements = await DetectAsync(
            new OcrWord("Nom:", 20, 100, 45, 20),
            new OcrWord("Mot", 200, 200, 30, 18),
            new OcrWord("de", 235, 200, 20, 18),
            new OcrWord("passe:", 260, 200, 55, 18));

        Assert.Equal(4, elements.Count);
        Assert.Equal("input_Nom", elements[1].Label);
        Assert.Equal("Mot de passe:", elements[2].Label);
        Assert.Equal(UiElementType.Input, elements[3].Type);
        Assert.Equal("input_Mot de passe", elements[3].Label);
    }
}

public sealed class UiElementMatcherTests
{
    [Fact]
    public void Matches_exact_label_case_insensitive()
    {
        var element = Button("OK");
        var result = UiElementMatcher.BestMatch(new[] { element }, "ok");
        Assert.Same(element, result);
    }

    [Fact]
    public void Matches_short_element_label_inside_query()
    {
        var send = Button("Send");
        var cancel = Button("Cancel");
        var result = UiElementMatcher.BestMatch(new[] { send, cancel }, "send message");
        Assert.Same(send, result);
    }

    [Fact]
    public void Matches_with_diacritics_normalization()
    {
        var ecole = Button("École");
        var result = UiElementMatcher.BestMatch(new[] { ecole }, "ecole");
        Assert.Same(ecole, result);
    }

    [Fact]
    public void Prefers_exact_over_partial()
    {
        var full = Button("Envoyer");
        var partial = Button("Envoyer le message");
        var result = UiElementMatcher.BestMatch(new[] { partial, full }, "Envoyer");
        Assert.Same(full, result);
    }

    [Fact]
    public void Returns_null_when_no_match()
    {
        var result = UiElementMatcher.BestMatch(new[] { Button("OK") }, "téléporter");
        Assert.Null(result);
    }

    [Fact]
    public void Returns_null_for_empty_query_or_list()
    {
        Assert.Null(UiElementMatcher.BestMatch(new[] { Button("OK") }, "   "));
        Assert.Null(UiElementMatcher.BestMatch(Array.Empty<UiElement>(), "OK"));
    }

    private static UiElement Button(string label) => new(1, UiElementType.Button, label, 0, 0, 40, 20, 20, 10, 0.7);
}

internal sealed class FakeOcr : IOcrService
{
    public bool Available { get; set; } = true;
    public OcrResult? Result { get; set; }

    public bool OcrAvailable => Available;

    public Task<OcrResult?> ExtractTextAsync(byte[] imageBytes, string language = "fra+eng", CancellationToken cancellationToken = default)
        => Task.FromResult(Available ? Result : null);
}
