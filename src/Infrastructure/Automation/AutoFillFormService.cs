using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Automation;

public interface IAutoFillFormService
{
    Task<AutoFillResult> FillFormFromDataAsync(string dataFilePath, string htmlFormPath, string? outputPath = null, CancellationToken ct = default);
    Task<string> GenerateFillSummaryAsync(string dataFilePath, string htmlFormPath, CancellationToken ct = default);
}

public sealed class AutoFillFormService : IAutoFillFormService
{
    private readonly ILogger<AutoFillFormService> _logger;

    private static readonly string[] MismatchKeys = new[]
    {
        "motdepasse", "password", "passwd", "confirmation", "confirm", "captcha", "csrf", "token", "_token",
        "hidden", "submit", "envoyer"
    };

    public AutoFillFormService(ILogger<AutoFillFormService> logger)
    {
        _logger = logger;
    }

    public async Task<AutoFillResult> FillFormFromDataAsync(string dataFilePath, string htmlFormPath, string? outputPath = null, CancellationToken ct = default)
    {
        var result = new AutoFillResult();

        if (!File.Exists(dataFilePath))
        {
            result.ErrorMessage = "Fichier de données introuvable : " + dataFilePath;
            return result;
        }
        if (!File.Exists(htmlFormPath))
        {
            result.ErrorMessage = "Fichier de formulaire HTML introuvable : " + htmlFormPath;
            return result;
        }

        try
        {
            var data = await LoadDataAsync(dataFilePath, ct);
            if (data.Count == 0)
            {
                result.ErrorMessage = "Aucune donnée trouvée dans le fichier source";
                return result;
            }

            var html = await File.ReadAllTextAsync(htmlFormPath, ct);
            var unmatched = new List<string>();
            var filled = 0;

            var fields = ExtractFields(html);
            result.FieldsTotal = fields.Count;

            foreach (var form in fields)
            {
                if (ct.IsCancellationRequested) break;

                var key = NormalizeKey(form.Item1);
                var value = FindValue(data, key, form.Item3);

                if (value is null)
                {
                    unmatched.Add(form.Item1);
                    continue;
                }

                html = ApplyValue(html, form.Item1, form.Item2, form.Item3, value);
                filled++;
            }

            var finalOutput = outputPath ?? Path.Combine(
                Path.GetDirectoryName(htmlFormPath) ?? "",
                $"{Path.GetFileNameWithoutExtension(htmlFormPath)}_filled.html");

            var dir = Path.GetDirectoryName(finalOutput);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(finalOutput, html, ct);

            result.Success = true;
            result.OutputPath = finalOutput;
            result.FieldsFilled = filled;
            result.UnmatchedFields = unmatched;

            _logger.LogInformation("[AutoFill] Formulaire rempli : {Filled}/{Total} champs → {Path}",
                filled, fields.Count, finalOutput);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[AutoFill] Échec du remplissage");
        }

        return result;
    }

    public async Task<string> GenerateFillSummaryAsync(string dataFilePath, string htmlFormPath, CancellationToken ct = default)
    {
        var result = await FillFormFromDataAsync(dataFilePath, htmlFormPath, null, ct);

        var sb = new StringBuilder();
        sb.AppendLine("=== Résumé du remplissage de formulaire ===");
        sb.AppendLine($"Formulaire : {htmlFormPath}");
        sb.AppendLine($"Données     : {dataFilePath}");
        sb.AppendLine();
        sb.AppendLine($"Champs remplis : {result.FieldsFilled}/{result.FieldsTotal}");
        sb.AppendLine();

        if (result.Success)
        {
            sb.AppendLine("Champs traités :");
            foreach (var unmatched in result.UnmatchedFields)
                sb.AppendLine($"  - {unmatched} (non trouvé dans les données)");
        }
        else
        {
            sb.AppendLine($"Erreur : {result.ErrorMessage}");
        }

        return sb.ToString();
    }

    private async Task<Dictionary<string, string>> LoadDataAsync(string dataFilePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(dataFilePath).ToLowerInvariant();
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (ext is ".json" or ".jsonc")
        {
            var json = await File.ReadAllTextAsync(dataFilePath, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    foreach (var prop in el.EnumerateObject())
                        if (!data.ContainsKey(prop.Name))
                            data[prop.Name] = prop.Value.ToString();
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                    data[prop.Name] = prop.Value.ToString();
            }
        }
        else if (ext is ".csv" or ".txt" or ".tsv")
        {
            var lines = (await File.ReadAllLinesAsync(dataFilePath, ct))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (lines.Count > 0)
            {
                var headers = SplitCsvLine(lines[0]);
                for (var i = 1; i < lines.Count; i++)
                {
                    var cells = SplitCsvLine(lines[i]);
                    for (var c = 0; c < headers.Length && c < cells.Length; c++)
                        if (!data.ContainsKey(headers[c]))
                            data[headers[c]] = cells[c];
                }
            }
        }

        return data;
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        if (line.Split(';').Length >= line.Split(',').Length)
            return line.Split(';');
        return line.Split(',');
    }

    private static List<(string Name, string Tag, string Type)> ExtractFields(string html)
    {
        var fields = new List<(string, string, string)>();

        foreach (Match match in Regex.Matches(html, "<(input|select|textarea)([^>]*)(/?)>", RegexOptions.IgnoreCase))
        {
            var tag = match.Groups[1].Value.ToLowerInvariant();
            var attrs = match.Groups[2].Value;

            var name = GetAttr(attrs, "name");
            var type = GetAttr(attrs, "type") ?? tag;
            if (string.IsNullOrEmpty(name))
            {
                var id = GetAttr(attrs, "id");
                if (!string.IsNullOrEmpty(id)) name = id;
            }
            if (string.IsNullOrEmpty(name)) continue;

            if (IsMismatchKey(name)) continue;

            fields.Add((name, tag, type));
        }

        return fields;
    }

    private static bool IsMismatchKey(string name)
    {
        var n = NormalizeKey(name);
        foreach (var k in MismatchKeys)
            if (n.Contains(NormalizeKey(k)))
                return true;
        return false;
    }

    private static string? FindValue(Dictionary<string, string> data, string normalizedKey, string tagType)
    {
        foreach (var kv in data)
        {
            if (NormalizeKey(kv.Key) == normalizedKey)
                return kv.Value;
        }

        foreach (var kv in data)
        {
            var nk = NormalizeKey(kv.Key);
            if (nk.Contains(normalizedKey) || normalizedKey.Contains(nk))
                return kv.Value;
        }

        return null;
    }

    private static string ApplyValue(string html, string name, string tag, string type, string value)
    {
        var encoded = HtmlEncode(value);
        var pattern = $"<(input|textarea)[^>]*\\bname=[\"']{Regex.Escape(name)}[\"'][^>]*>";

        if (tag == "select")
        {
            return MarkSelectOption(html, name, value);
        }

        if (tag == "input" && (type == "checkbox" || type == "radio"))
        {
            var isChecked = IsTruthy(value);
            if (isChecked)
                return Regex.Replace(html, pattern, m => AddAttribute(m.Value, "checked", "checked"), RegexOptions.IgnoreCase);
            return html;
        }

        return Regex.Replace(html, pattern, m => SetValueInTag(m.Value, encoded), RegexOptions.IgnoreCase);
    }

    private static string MarkSelectOption(string html, string name, string value)
    {
        var selectPattern = $"<select[^>]*\\bname=[\"']{Regex.Escape(name)}[\"'][^>]*>[\\s\\S]*?</select>";
        var match = Regex.Match(html, selectPattern, RegexOptions.IgnoreCase);
        if (!match.Success) return html;

        var selectBlock = match.Value;
        var optionPattern = "<option([^>]*)>([\\s\\S]*?)</option>";
        var newBlock = Regex.Replace(selectBlock, optionPattern, op =>
        {
            var attrs = op.Groups[1].Value;
            var text = op.Groups[2].Value;
            var optValue = GetAttr(attrs, "value") ?? StripTags(text);
            if (NormalizeKey(optValue) == NormalizeKey(value))
            {
                attrs = SetAttr(attrs, "selected", "selected");
                return $"<option{attrs}>{text}</option>";
            }
            return op.Value;
        }, RegexOptions.IgnoreCase);

        return Regex.Replace(html, selectPattern, newBlock, RegexOptions.IgnoreCase);
    }

    private static string SetValueInTag(string tag, string encodedValue)
    {
        if (Regex.IsMatch(tag, "\\bvalue\\s*=", RegexOptions.IgnoreCase))
            return Regex.Replace(tag, "(\\bvalue\\s*=\\s*)[\"'][^\"']*[\"']",
                m => m.Groups[1].Value + "\"" + encodedValue + "\"", RegexOptions.IgnoreCase);

        return Regex.Replace(tag, ">$", " value=\"" + encodedValue + "\">");
    }

    private static string AddAttribute(string tag, string attrName, string attrValue)
    {
        if (Regex.IsMatch(tag, $"\\b{Regex.Escape(attrName)}\\s*=", RegexOptions.IgnoreCase))
            return tag;
        return Regex.Replace(tag, ">$", " " + attrName + "=\"" + attrValue + "\">");
    }

    private static string SetAttr(string attrs, string name, string value)
    {
        if (Regex.IsMatch(attrs, $"\\b{Regex.Escape(name)}\\s*=", RegexOptions.IgnoreCase))
            return Regex.Replace(attrs, $"(\\b{Regex.Escape(name)}\\s*=\\s*)[\"'][^\"']*[\"']",
                m => m.Groups[1].Value + "\"" + value + "\"", RegexOptions.IgnoreCase);
        var sel = attrs.TrimEnd();
        return sel + " " + name + "=\"" + value + "\"";
    }

    private static string? GetAttr(string attrs, string name)
    {
        var match = Regex.Match(attrs, $"\\b{Regex.Escape(name)}\\s*=\\s*[\"']?([^\"'\\s>]*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsTruthy(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v is "true" or "1" or "oui" or "yes" or "on" or "checked";
    }

    private static string StripTags(string html)
    {
        return Regex.Replace(Regex.Replace(html, "<[^>]+>", ""), "\\s+", " ").Trim();
    }

    private static string NormalizeKey(string key)
    {
        var normalized = key.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c) || char.IsNumber(c))
                sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant();
    }

    private static string HtmlEncode(string value)
    {
        return value.Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}

public sealed class AutoFillResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public int FieldsFilled { get; set; }
    public int FieldsTotal { get; set; }
    public List<string> UnmatchedFields { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
