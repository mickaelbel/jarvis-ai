using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Automation;

public interface IDocumentTranslatorService
{
    Task<TranslationResult> TranslateFileAsync(string inputPath, string targetLanguage, string? outputPath = null, CancellationToken ct = default);
    Task<string> TranslateTextAsync(string text, string targetLanguage, CancellationToken ct = default);
    Task<List<string>> GetSupportedLanguages();
}

public sealed class DocumentTranslatorService : IDocumentTranslatorService
{
    private readonly ILogger<DocumentTranslatorService> _logger;
    private readonly HttpClient _httpClient;

    private static readonly Dictionary<string, string> Languages = new()
    {
        ["fr"] = "Français",
        ["en"] = "English",
        ["es"] = "Español",
        ["de"] = "Deutsch",
        ["it"] = "Italiano",
        ["pt"] = "Português",
        ["nl"] = "Nederlands",
        ["ru"] = "Русский",
        ["zh"] = "中文",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
        ["ar"] = "العربية"
    };

    public DocumentTranslatorService(ILogger<DocumentTranslatorService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<TranslationResult> TranslateFileAsync(string inputPath, string targetLanguage, string? outputPath = null, CancellationToken ct = default)
    {
        var result = new TranslationResult { InputPath = inputPath, TargetLanguage = targetLanguage };

        if (!File.Exists(inputPath))
        {
            result.ErrorMessage = $"File not found: {inputPath}";
            return result;
        }

        try
        {
            var text = await File.ReadAllTextAsync(inputPath, ct);
            var translated = await TranslateTextAsync(text, targetLanguage, ct);

            var output = outputPath ?? Path.Combine(
                Path.GetDirectoryName(inputPath) ?? "",
                $"{Path.GetFileNameWithoutExtension(inputPath)}_{targetLanguage}{Path.GetExtension(inputPath)}");

            await File.WriteAllTextAsync(output, translated, ct);

            result.Success = true;
            result.OutputPath = output;
            result.WordsTranslated = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            _logger.LogInformation("[Translator] Translated: {Input} → {Output}", inputPath, output);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Translator] Failed: {Input}", inputPath);
        }

        return result;
    }

    public async Task<string> TranslateTextAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
        // Use MyMemory API (free)
        try
        {
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair=fr|{targetLanguage}";
            var response = await _httpClient.GetStringAsync(url, ct);
            var doc = System.Text.Json.JsonDocument.Parse(response);
            var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
            return translated ?? text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Translator] API failed, returning original");
            return text;
        }
    }

    public Task<List<string>> GetSupportedLanguages()
    {
        return Task.FromResult(Languages.Keys.ToList());
    }
}

public sealed class TranslationResult
{
    public bool Success { get; set; }
    public string InputPath { get; set; } = "";
    public string? OutputPath { get; set; }
    public string TargetLanguage { get; set; } = "";
    public int WordsTranslated { get; set; }
    public string? ErrorMessage { get; set; }
}
