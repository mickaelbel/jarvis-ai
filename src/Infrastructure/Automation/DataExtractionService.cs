using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace JarvisAI.Infrastructure.Automation;

public interface IDataExtractionService
{
    Task<DataExtractionResult> ExtractTableFromUrlAsync(string url, int tableIndex = 0, string format = "csv", string? outputPath = null, CancellationToken ct = default);
    Task<DataExtractionResult> ExtractTableFromHtmlAsync(string html, int tableIndex = 0, string format = "csv", string? outputPath = null, CancellationToken ct = default);
}

public sealed class DataExtractionService : IDataExtractionService
{
    private readonly ILogger<DataExtractionService> _logger;
    private readonly HttpClient _httpClient;

    public DataExtractionService(ILogger<DataExtractionService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<DataExtractionResult> ExtractTableFromUrlAsync(string url, int tableIndex = 0, string format = "csv", string? outputPath = null, CancellationToken ct = default)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url, ct);
            return await ExtractTableFromHtmlAsync(html, tableIndex, format, outputPath, ct);
        }
        catch (Exception ex)
        {
            return new DataExtractionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task<DataExtractionResult> ExtractTableFromHtmlAsync(string html, int tableIndex = 0, string format = "csv", string? outputPath = null, CancellationToken ct = default)
    {
        return Task.FromResult(BuildExtractionResult(html, tableIndex, format, outputPath));
    }

    private DataExtractionResult BuildExtractionResult(string html, int tableIndex, string format, string? outputPath)
    {
        var result = new DataExtractionResult();
        var requestedFormat = format?.ToLowerInvariant() ?? "csv";
        var usedFallback = false;

        try
        {
            var tables = ExtractTables(html);
            if (tableIndex < 0 || tableIndex >= tables.Count)
            {
                result.ErrorMessage = $"Tableau index {tableIndex} introuvable (seulement {tables.Count} tableau(x) trouvé(s))";
                return result;
            }

            var tableHtml = tables[tableIndex];
            var rows = ParseTable(tableHtml);

            if (rows.Count == 0)
            {
                result.ErrorMessage = "Aucune ligne de données trouvée dans le tableau";
                return result;
            }

            var columns = rows[0].Count;
            result.Rows = rows.Count;
            result.Columns = columns;

            string? finalOutput;
            if (requestedFormat == "xlsx")
            {
                try
                {
                    finalOutput = WriteXlsx(rows, outputPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DataExtraction] XLSX échoué, fallback CSV");
                    usedFallback = true;
                    finalOutput = WriteCsv(rows, outputPath);
                }
            }
            else
            {
                finalOutput = WriteCsv(rows, outputPath);
            }

            if (finalOutput is null)
            {
                result.ErrorMessage = "Impossible d'écrire le fichier de sortie";
                return result;
            }

            result.Success = true;
            result.OutputPath = finalOutput;
            result.Preview = BuildPreview(rows);
            result.ErrorMessage = usedFallback ? "Format xlsx indisponible, sauvegarde au format csv" : null;

            _logger.LogInformation("[DataExtraction] Table extraite : {Rows} lignes x {Cols} colonnes → {Path}",
                rows.Count, columns, finalOutput);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[DataExtraction] Échec de l'extraction");
        }

        return result;
    }

    private static List<string> ExtractTables(string html)
    {
        var tables = new List<string>();
        var matches = Regex.Matches(html, "<table[^>]*>[\\s\\S]*?</table>", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
            tables.Add(match.Value);
        return tables;
    }

    private static List<List<string>> ParseTable(string tableHtml)
    {
        var rows = new List<List<string>>();
        var rowMatches = Regex.Matches(tableHtml, "<tr[^>]*>([\\s\\S]*?)</tr>", RegexOptions.IgnoreCase);

        foreach (Match rowMatch in rowMatches)
        {
            var row = new List<string>();
            var cellMatches = Regex.Matches(rowMatch.Groups[1].Value, "<(?:th|td)[^>]*>([\\s\\S]*?)</(?:th|td)>", RegexOptions.IgnoreCase);

            foreach (Match cellMatch in cellMatches)
            {
                var cellText = CellText(cellMatch.Groups[1].Value);
                row.Add(cellText);
            }

            if (row.Count > 0)
                rows.Add(row);
        }

        return rows;
    }

    private static string CellText(string inner)
    {
        inner = Regex.Replace(inner, "<[^>]+>", " ");
        inner = System.Net.WebUtility.HtmlDecode(inner);
        return Regex.Replace(inner, "\\s+", " ").Trim();
    }

    private static string? WriteCsv(List<List<string>> rows, string? outputPath)
    {
        var output = outputPath ?? Path.Combine(Path.GetTempPath(), $"table_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        EnsureDir(output);

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(";", row.Select(CsvEscape)));
        }

        File.WriteAllBytes(output, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
        return output;
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static string? WriteXlsx(List<List<string>> rows, string? outputPath)
    {
        var output = outputPath ?? Path.Combine(Path.GetTempPath(), $"table_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        EnsureDir(output);
        if (File.Exists(output)) File.Delete(output);

        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);

        WriteEntry(archive, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>");

        WriteEntry(archive, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        WriteEntry(archive, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Feuille1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            "</workbook>");

        WriteEntry(archive, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "</Relationships>");

        var sheetXml = BuildSheetXml(rows);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", sheetXml);

        return output;
    }

    private static string BuildSheetXml(List<List<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        var rowIdx = 1;
        foreach (var row in rows)
        {
            sb.Append($"<row r=\"{rowIdx}\">");
            var colIdx = 0;
            foreach (var cell in row.Take(26))
            {
                var col = GetColumnName(colIdx);
                sb.Append($"<c r=\"{col}{rowIdx}\" t=\"inlineStr\"><is><t>{XmlEscape(cell)}</t></is></c>");
                colIdx++;
            }
            sb.Append("</row>");
            rowIdx++;
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string GetColumnName(int index)
    {
        var name = "";
        var value = index;
        do
        {
            name = (char)('A' + (value % 26)) + name;
            value = value / 26 - 1;
        } while (value >= 0);
        return name;
    }

    private static string XmlEscape(string value)
    {
        return value.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string BuildPreview(List<List<string>> rows)
    {
        var preview = new StringBuilder();
        foreach (var row in rows.Take(5))
        {
            preview.AppendLine(string.Join(" | ", row));
        }
        if (rows.Count > 5)
            preview.AppendLine($"... et {rows.Count - 5} ligne(s) supplémentaires");
        return preview.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void EnsureDir(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}

public sealed class DataExtractionResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public int Rows { get; set; }
    public int Columns { get; set; }
    public string? Preview { get; set; }
    public string? ErrorMessage { get; set; }
}
