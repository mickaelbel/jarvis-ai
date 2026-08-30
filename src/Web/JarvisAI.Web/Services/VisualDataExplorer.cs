using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IVisualDataExplorer
{
    DataImportResult ImportData(string filePath);
    DataSummary SummarizeData(string dataId);
    string GenerateChart(string dataId, ChartType chartType, string? xAxis = null, string? yAxis = null);
    IReadOnlyList<DataColumn> GetColumns(string dataId);
    string ExportData(string dataId, string format);
}

public sealed class VisualDataExplorer : IVisualDataExplorer
{
    private readonly ILogger<VisualDataExplorer> _logger;
    private readonly string _storagePath;
    private readonly Dictionary<string, DataSet> _datasets = new();

    public VisualDataExplorer(ILogger<VisualDataExplorer> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "datasets.json");
    }

    public DataImportResult ImportData(string filePath)
    {
        if (!File.Exists(filePath))
            return new DataImportResult { Success = false, Error = "Fichier non trouvé" };

        var content = File.ReadAllText(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        var dataset = ext switch
        {
            ".csv" => ParseCsv(content),
            ".json" => ParseJson(content),
            _ => new DataSet { Name = Path.GetFileName(filePath) }
        };

        dataset.Id = Guid.NewGuid().ToString("N")[..8];
        dataset.SourceFile = filePath;
        dataset.ImportedAt = DateTime.UtcNow;

        _datasets[dataset.Id] = dataset;

        return new DataImportResult
        {
            Success = true,
            DataId = dataset.Id,
            RowCount = dataset.Rows.Count,
            ColumnCount = dataset.Columns.Count,
            Preview = dataset.Rows.Take(5).ToList()
        };
    }

    public DataSummary SummarizeData(string dataId)
    {
        if (!_datasets.TryGetValue(dataId, out var dataset))
            return new DataSummary { Error = "Dataset non trouvé" };

        var summary = new DataSummary
        {
            DataId = dataId,
            Name = dataset.Name,
            RowCount = dataset.Rows.Count,
            ColumnCount = dataset.Columns.Count,
            Columns = dataset.Columns.Select(c => new ColumnSummary
            {
                Name = c.Name,
                Type = c.Type,
                NonNullCount = dataset.Rows.Count(r => r.ContainsKey(c.Name) && r[c.Name] != null),
                UniqueValues = dataset.Rows.Select(r => r.GetValueOrDefault(c.Name)?.ToString()).Distinct().Count()
            }).ToList()
        };

        return summary;
    }

    public string GenerateChart(string dataId, ChartType chartType, string? xAxis = null, string? yAxis = null)
    {
        if (!_datasets.TryGetValue(dataId, out var dataset))
            return "Dataset non trouvé";

        var sb = new StringBuilder();
        sb.AppendLine($"## Graphique: {chartType}");
        sb.AppendLine($"Données: {dataset.Name} ({dataset.Rows.Count} lignes)");
        sb.AppendLine();

        switch (chartType)
        {
            case ChartType.BarChart:
                sb.AppendLine("```chart");
                sb.AppendLine("type: bar");
                if (xAxis is not null) sb.AppendLine($"x: {xAxis}");
                if (yAxis is not null) sb.AppendLine($"y: {yAxis}");
                sb.AppendLine("```");
                break;

            case ChartType.PieChart:
                sb.AppendLine("```chart");
                sb.AppendLine("type: pie");
                if (xAxis is not null) sb.AppendLine($"labels: {xAxis}");
                if (yAxis is not null) sb.AppendLine($"values: {yAxis}");
                sb.AppendLine("```");
                break;

            case ChartType.LineChart:
                sb.AppendLine("```chart");
                sb.AppendLine("type: line");
                if (xAxis is not null) sb.AppendLine($"x: {xAxis}");
                if (yAxis is not null) sb.AppendLine($"y: {yAxis}");
                sb.AppendLine("```");
                break;

            default:
                sb.AppendLine("Type de graphique non supporté");
                break;
        }

        return sb.ToString();
    }

    public IReadOnlyList<DataColumn> GetColumns(string dataId)
    {
        if (!_datasets.TryGetValue(dataId, out var dataset))
            return new List<DataColumn>();

        return dataset.Columns;
    }

    public string ExportData(string dataId, string format)
    {
        if (!_datasets.TryGetValue(dataId, out var dataset))
            return "Dataset non trouvé";

        return format.ToLowerInvariant() switch
        {
            "csv" => ExportCsv(dataset),
            "json" => ExportJson(dataset),
            _ => "Format non supporté"
        };
    }

    private DataSet ParseCsv(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new DataSet();

        var headers = lines[0].Split(',');
        var columns = headers.Select(h => new DataColumn
        {
            Name = h.Trim(),
            Type = "string"
        }).ToList();

        var rows = new List<Dictionary<string, object?>>();
        for (int i = 1; i < lines.Length; i++)
        {
            var values = lines[i].Split(',');
            var row = new Dictionary<string, object?>();
            for (int j = 0; j < Math.Min(headers.Length, values.Length); j++)
            {
                row[headers[j].Trim()] = values[j].Trim();
            }
            rows.Add(row);
        }

        return new DataSet
        {
            Columns = columns,
            Rows = rows
        };
    }

    private DataSet ParseJson(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var first = root[0];
                var columns = new List<DataColumn>();
                var rows = new List<Dictionary<string, object?>>();

                foreach (var prop in first.EnumerateObject())
                {
                    columns.Add(new DataColumn
                    {
                        Name = prop.Name,
                        Type = prop.Value.ValueKind.ToString()
                    });
                }

                foreach (var item in root.EnumerateArray())
                {
                    var row = new Dictionary<string, object?>();
                    foreach (var prop in item.EnumerateObject())
                    {
                        row[prop.Name] = prop.Value.ToString();
                    }
                    rows.Add(row);
                }

                return new DataSet { Columns = columns, Rows = rows };
            }
        }
        catch { }

        return new DataSet();
    }

    private string ExportCsv(DataSet dataset)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", dataset.Columns.Select(c => c.Name)));

        foreach (var row in dataset.Rows)
        {
            var values = dataset.Columns.Select(c => row.GetValueOrDefault(c.Name)?.ToString() ?? "");
            sb.AppendLine(string.Join(",", values));
        }

        return sb.ToString();
    }

    private string ExportJson(DataSet dataset)
    {
        return JsonSerializer.Serialize(dataset.Rows, new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed class DataSet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public List<DataColumn> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public DateTime ImportedAt { get; set; }
}

public sealed class DataColumn
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
}

public sealed class DataImportResult
{
    public bool Success { get; set; }
    public string? DataId { get; set; }
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public List<Dictionary<string, object?>> Preview { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class DataSummary
{
    public string? DataId { get; set; }
    public string Name { get; set; } = "";
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public List<ColumnSummary> Columns { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class ColumnSummary
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int NonNullCount { get; set; }
    public int UniqueValues { get; set; }
}

public enum ChartType { BarChart, PieChart, LineChart, ScatterPlot }
