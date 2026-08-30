using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.AI;

public interface ICodeFormatter
{
    string FormatCode(string code, string language, FormattingStyle style = FormattingStyle.Default);
    FormattingDiff GetDiff(string original, string formatted);
    CodeStylePreset GetPreset(string presetName);
    IReadOnlyList<string> GetAvailablePresets(string language);
}

public sealed class CodeFormatter : ICodeFormatter
{
    private readonly ILogger<CodeFormatter> _logger;

    public CodeFormatter(ILogger<CodeFormatter> logger)
    {
        _logger = logger;
    }

    public string FormatCode(string code, string language, FormattingStyle style = FormattingStyle.Default)
    {
        var lang = language.ToLowerInvariant();
        return lang switch
        {
            "csharp" or "cs" => FormatCSharp(code, style),
            "javascript" or "js" => FormatJavaScript(code, style),
            "python" or "py" => FormatPython(code, style),
            "json" => FormatJson(code),
            _ => code
        };
    }

    public FormattingDiff GetDiff(string original, string formatted)
    {
        var originalLines = original.Split('\n');
        var formattedLines = formatted.Split('\n');

        var added = formattedLines.Except(originalLines).ToList();
        var removed = originalLines.Except(formattedLines).ToList();

        return new FormattingDiff
        {
            Original = original,
            Formatted = formatted,
            LinesAdded = added.Count,
            LinesRemoved = removed.Count,
            AddedLines = added,
            RemovedLines = removed,
            Changed = original != formatted
        };
    }

    public CodeStylePreset GetPreset(string presetName)
    {
        return presetName.ToLowerInvariant() switch
        {
            "default" or "standard" => new CodeStylePreset
            {
                Name = "Standard",
                IndentSize = 4,
                UseSpaces = true,
                MaxLineLength = 120,
                NewlineAfterBrace = true,
                TrailingComma = false
            },
            "compact" => new CodeStylePreset
            {
                Name = "Compact",
                IndentSize = 2,
                UseSpaces = true,
                MaxLineLength = 100,
                NewlineAfterBrace = false,
                TrailingComma = false
            },
            "strict" => new CodeStylePreset
            {
                Name = "Strict",
                IndentSize = 4,
                UseSpaces = true,
                MaxLineLength = 100,
                NewlineAfterBrace = true,
                TrailingComma = true
            },
            _ => GetPreset("default")
        };
    }

    public IReadOnlyList<string> GetAvailablePresets(string language)
    {
        return new[] { "default", "compact", "strict" };
    }

    private string FormatCSharp(string code, FormattingStyle style)
    {
        var lines = code.Split('\n');
        var formatted = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Normalize indentation
            var indent = line.Length - trimmed.Length;
            var normalizedIndent = (indent / 4) * 4;
            formatted.Append(new string(' ', normalizedIndent));
            formatted.AppendLine(trimmed);
        }

        // Normalize braces
        var result = formatted.ToString();
        result = Regex.Replace(result, @"\{\s*\n\s*\}", "{}");
        result = Regex.Replace(result, @"(\w)\s*\{", "$1 {");
        result = Regex.Replace(result, @"\}\s*(\w)", "}\n$1");

        return result.Trim();
    }

    private string FormatJavaScript(string code, FormattingStyle style)
    {
        // Similar to C# but with different conventions
        return FormatCSharp(code, style);
    }

    private string FormatPython(string code, FormattingStyle style)
    {
        var lines = code.Split('\n');
        var formatted = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Python uses 4 spaces
            var indent = line.Length - trimmed.Length;
            var normalizedIndent = (indent / 4) * 4;
            formatted.Append(new string(' ', normalizedIndent));
            formatted.AppendLine(trimmed);
        }

        return formatted.ToString().Trim();
    }

    private string FormatJson(string code)
    {
        try
        {
            var obj = System.Text.Json.JsonSerializer.Deserialize<object>(code);
            return System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch
        {
            return code;
        }
    }
}

public sealed class FormattingDiff
{
    public string Original { get; set; } = "";
    public string Formatted { get; set; } = "";
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public List<string> AddedLines { get; set; } = new();
    public List<string> RemovedLines { get; set; } = new();
    public bool Changed { get; set; }
}

public sealed class CodeStylePreset
{
    public string Name { get; set; } = "";
    public int IndentSize { get; set; } = 4;
    public bool UseSpaces { get; set; } = true;
    public int MaxLineLength { get; set; } = 120;
    public bool NewlineAfterBrace { get; set; } = true;
    public bool TrailingComma { get; set; }
}

public enum FormattingStyle { Default, Compact, Strict }
