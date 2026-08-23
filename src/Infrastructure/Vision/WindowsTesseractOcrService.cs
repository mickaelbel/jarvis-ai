using JarvisAI.Application.Vision;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Pix;

namespace JarvisAI.Infrastructure.Vision;

public sealed class WindowsTesseractOcrService : IOcrService
{
    private readonly ILogger<WindowsTesseractOcrService> _logger;
    private readonly string _tessdataPath;

    public WindowsTesseractOcrService(ILogger<WindowsTesseractOcrService> logger, string? tessdataPath = null)
    {
        _logger = logger;
        _tessdataPath = tessdataPath ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    public bool OcrAvailable =>
        OperatingSystem.IsWindows() && Directory.Exists(_tessdataPath);

    public async Task<OcrResult?> ExtractTextAsync(byte[] imageBytes, string language = "fra+eng", CancellationToken cancellationToken = default)
    {
        if (!OcrAvailable)
        {
            _logger.LogWarning("[Ocr] OCR unavailable (non-Windows or tessdata missing at {Path})", _tessdataPath);
            return null;
        }

        if (imageBytes is null || imageBytes.Length == 0)
            return null;

        return await Task.Run(() => ExtractTextCoreSafe(imageBytes, language), cancellationToken);
    }

    private void SetupOcrEngine(Engine engine)
    {
        engine.SetVariable("tessedit_write_raw_output", "0");
        engine.SetVariable("tessedit_zero_baseline", "1");
        engine.SetVariable("textord_min_xheight", "0");
        engine.SetVariable("textord_min_descenders", "0");
        engine.SetVariable("textord_min_ascent", "0");
    }

    private OcrResult? ExtractTextCoreSafe(byte[] imageBytes, string language)
    {
        try
        {
            return ExtractTextCoreInternal(imageBytes, language);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ocr] OCR processing failed");
            return null;
        }
    }

    private OcrResult? ExtractTextCoreInternal(byte[] imageBytes, string language)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        using var engine = new Engine(_tessdataPath, language, EngineMode.LstmOnly);
        SetupOcrEngine(engine);

        using var image = Image.LoadFromMemory(imageBytes);
        using var page = engine.Process(image);

        var text = page.Text ?? string.Empty;
        var words = new List<OcrWord>();

        foreach (var block in page.Layout)
        {
            foreach (var paragraph in block.Paragraphs)
            {
                foreach (var textLine in paragraph.TextLines)
                {
                    foreach (var word in textLine.Words)
                    {
                        var wordText = word.Text;
                        if (string.IsNullOrWhiteSpace(wordText))
                            continue;

                        var x = 0;
                        var y = 0;
                        var width = 0;
                        var height = 0;
                        if (word.BoundingBox is { } box)
                        {
                            x = box.X1;
                            y = box.Y1;
                            width = box.Width;
                            height = box.Height;
                        }

                        words.Add(new OcrWord(wordText, x, y, width, height));
                    }
                }
            }
        }

        _logger.LogInformation("[Ocr] Extracted {Words} words ({Length} chars)", words.Count, text.Length);
        if (words.Count > 0)
        {
            _logger.LogDebug("[Ocr] Sample words: {Sample}", string.Join(" | ", words.Take(10).Select(w => $"{w.Text}({w.X},{w.Y},{w.Width}x{w.Height})")));
        }
        return new OcrResult(text, words);
    }
}
