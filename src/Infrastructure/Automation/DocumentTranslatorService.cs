using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

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
            result.ErrorMessage = "Fichier introuvable : " + inputPath;
            return result;
        }

        try
        {
            var extension = Path.GetExtension(inputPath).ToLowerInvariant();

            if (extension == ".docx")
            {
                await TranslateDocxAsync(inputPath, targetLanguage, outputPath, result, ct);
            }
            else if (extension == ".pdf")
            {
                await TranslatePdfAsync(inputPath, targetLanguage, outputPath, result, ct);
            }
            else
            {
                await TranslateTextFileAsync(inputPath, targetLanguage, outputPath, result, ct);
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[Translator] Failed: {Input}", inputPath);
        }

        return result;
    }

    private async Task TranslateTextFileAsync(string inputPath, string targetLanguage, string? outputPath, TranslationResult result, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(inputPath, ct);
        var translated = await TraduireParMorceauxAsync(text, targetLanguage, ct);

        var output = outputPath ?? Path.Combine(
            Path.GetDirectoryName(inputPath) ?? "",
            $"{Path.GetFileNameWithoutExtension(inputPath)}_{targetLanguage}{Path.GetExtension(inputPath)}");

        await File.WriteAllTextAsync(output, translated, ct);

        result.Success = true;
        result.OutputPath = output;
        result.WordsTranslated = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        _logger.LogInformation("[Translator] Traduit : {Input} → {Output}", inputPath, output);
    }

    private async Task TranslateDocxAsync(string inputPath, string targetLanguage, string? outputPath, TranslationResult result, CancellationToken ct)
    {
        var paragraphs = ExtractParagraphsFromDocx(inputPath, ct);
        var translatedParagraphs = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            if (ct.IsCancellationRequested) break;
            var cleaned = paragraph.Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                translatedParagraphs.Add("");
                continue;
            }

            var translated = await TraduireParMorceauxAsync(cleaned, targetLanguage, ct);
            translatedParagraphs.Add(translated);
        }

        var output = outputPath ?? Path.Combine(
            Path.GetDirectoryName(inputPath) ?? "",
            $"{Path.GetFileNameWithoutExtension(inputPath)}_{targetLanguage}.docx");

        WriteDocx(output, translatedParagraphs);

        result.Success = true;
        result.OutputPath = output;
        result.WordsTranslated = paragraphs.Sum(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        _logger.LogInformation("[Translator] DOCX traduit : {Input} → {Output}", inputPath, output);
    }

    private async Task TranslatePdfAsync(string inputPath, string targetLanguage, string? outputPath, TranslationResult result, CancellationToken ct)
    {
        var text = ExtractPdfText(inputPath);
        if (text.Length < 20)
        {
            result.ErrorMessage = "PDF : aucun texte extractible (codé/numérisé)";
            _logger.LogWarning("[Translator] PDF sans texte extractible : {Input}", inputPath);
            return;
        }

        var translated = await TraduireParMorceauxAsync(text, targetLanguage, ct);

        var output = outputPath ?? Path.Combine(
            Path.GetDirectoryName(inputPath) ?? "",
            $"{Path.GetFileNameWithoutExtension(inputPath)}_{targetLanguage}.txt");

        await File.WriteAllTextAsync(output, translated, ct);

        result.Success = true;
        result.OutputPath = output;
        result.WordsTranslated = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        _logger.LogInformation("[Translator] PDF traduit : {Input} → {Output}", inputPath, output);
    }

    public async Task<string> TranslateTextAsync(string text, string targetLanguage, CancellationToken ct = default)
    {
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
            _logger.LogWarning(ex, "[Translator] API échouée, retour du texte original");
            return text;
        }
    }

    private async Task<string> TraduireParMorceauxAsync(string text, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (text.Length <= 400)
        {
            return await TranslateTextAsync(text, targetLanguage, ct);
        }

        var chunks = new List<string>();
        var remaining = text;
        while (remaining.Length > 0)
        {
            var take = Math.Min(380, remaining.Length);
            chunks.Add(remaining[..take]);
            remaining = remaining[take..];
        }

        var builder = new StringBuilder();
        foreach (var chunk in chunks)
        {
            if (ct.IsCancellationRequested) break;
            var translated = await TranslateTextAsync(chunk, targetLanguage, ct);
            builder.Append(translated);
        }

        return builder.ToString().Trim();
    }

    public Task<List<string>> GetSupportedLanguages()
    {
        return Task.FromResult(Languages.Keys.ToList());
    }

    private static List<string> ExtractParagraphsFromDocx(string inputPath, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(inputPath);
        var docEntry = archive.GetEntry("word/document.xml");
        if (docEntry is null) return new List<string>();

        using var reader = new StreamReader(docEntry.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        return ExtractParagraphsFromDocumentXml(xml);
    }

    private static List<string> ExtractParagraphsFromDocumentXml(string xml)
    {
        var paragraphs = new List<string>();
        var pMatches = Regex.Matches(xml, "<w:p[ >].*?</w:p>", RegexOptions.Singleline);

        foreach (Match pMatch in pMatches)
        {
            var p = pMatch.Value;
            var sb = new StringBuilder();

            foreach (Match tMatch in Regex.Matches(p, "<w:t[^>]*>(.*?)</w:t>", RegexOptions.Singleline))
            {
                sb.Append(tMatch.Groups[1].Value);
            }

            paragraphs.Add(sb.ToString());
        }

        return paragraphs;
    }

    private static void WriteDocx(string outputPath, List<string> paragraphs)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(outputPath)) File.Delete(outputPath);

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        var contentTypes = archive.CreateEntry("[Content_Types].xml");
        using (var w = new StreamWriter(contentTypes.Open(), new UTF8Encoding(false)))
        {
            w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>");
        }

        var rels = archive.CreateEntry("_rels/.rels");
        using (var w = new StreamWriter(rels.Open(), new UTF8Encoding(false)))
        {
            w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");
        }

        var docEntry = archive.CreateEntry("word/document.xml");
        using (var w = new StreamWriter(docEntry.Open(), new UTF8Encoding(false)))
        {
            const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
            w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {ns}><w:body>");

            foreach (var paragraph in paragraphs)
            {
                var escaped = XMLEscape(paragraph);
                w.Write("<w:p><w:r><w:t xml:space=\"preserve\">" + escaped + "</w:t></w:r></w:p>");
            }

            w.Write("</w:body></w:document>");
        }
    }

    private static string XMLEscape(string value)
    {
        return value?.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;") ?? "";
    }

    private static string ExtractPdfText(string inputPath)
    {
        var info = new FileInfo(inputPath);
        if (info.Length > 2L * 1024 * 1024) return "";

        var bytes = File.ReadAllBytes(inputPath);
        var content = Encoding.Latin1.GetString(bytes);

        var text = new StringBuilder();
        var contentStart = content.IndexOf("stream", StringComparison.OrdinalIgnoreCase);
        if (contentStart < 0) contentStart = 0;
        var contentEnd = content.LastIndexOf("endstream", StringComparison.OrdinalIgnoreCase);
        if (contentEnd < 0) contentEnd = content.Length;
        var body = content.Substring(contentStart, contentEnd - contentStart);

        var matches = Regex.Matches(body, "\\(((?:\\\\.|[^\\\\()])*)\\)");

        foreach (Match match in matches)
        {
            var contextStart = Math.Max(0, match.Index - 200);
            var context = body.AsSpan(contextStart, match.Index - contextStart);
            if (!context.Contains("Tj", StringComparison.Ordinal)
                && !context.Contains("TJ", StringComparison.Ordinal)
                && !context.Contains("Tf", StringComparison.Ordinal))
                continue;

            var value = UnescapePdfString(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(value))
                text.Append(' ').Append(value);
        }

        return Regex.Replace(text.ToString(), "\\s+", " ").Trim();
    }

    private static string UnescapePdfString(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next is '(' or ')' or '\\')
                {
                    sb.Append(next);
                    i++;
                }
                else if (next == 'n')
                {
                    sb.Append('\n');
                    i++;
                }
                else if (next == 'r')
                {
                    sb.Append('\r');
                    i++;
                }
                else if (next == 't')
                {
                    sb.Append('\t');
                    i++;
                }
                else
                {
                    sb.Append(next);
                    i++;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
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