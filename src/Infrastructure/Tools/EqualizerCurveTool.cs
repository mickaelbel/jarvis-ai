using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Tools;

/// <summary>
/// Analyse un fichier de courbe d'égaliseur exporté (Wavelet / FilterCurve,
/// p. ex. bass.txt). Parse les bandes fN (fréquence en Hz) et vN (gain en dB)
/// et renvoie des valeurs FACTUELLES (pic, plage du boost, longueur du filtre)
/// pour éviter que le modèle invente des interprétations.
/// </summary>
public sealed class EqualizerCurveTool : ITool
{
    private static readonly Regex KeyValueRegex = new(
        @"(?<key>[A-Za-z][A-Za-z0-9]*)\s*=\s*\""(?<value>[^\""]*)\""",
        RegexOptions.Compiled);

    public string Name => "equalizer_curve";
    public string Description =>
        "Analyse un fichier de courbe d'égaliseur (export Wavelet / FilterCurve, contenu du type " +
        "FilterCurve:f0=\"10\" v0=\"2.1\" ... FilterLength=\"8191\"). Renvoie les VRAIES valeurs : " +
        "bandes de fréquences en Hz, gains en dB, gain maximal et sa fréquence, plage où le boost " +
        "s'applique, et longueur du filtre (nombre de coefficients). Utilise cet outil dès que " +
        "l'utilisateur demande d'analyser un fichier de courbe d'égaliseur/équalizer.";
    public string Category => "filesystem";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("path", "Chemin du fichier de courbe d'égaliseur (p. ex. Bureau\\bass.txt)", typeof(string), required: true)
    };

    public Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        parameters.TryGetValue("path", out var rawPath);
        var path = UserPaths.ResolveUserPath(rawPath);
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Failed("Parameter 'path' is required"));

        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Failed($"File not found: {path}"));

        string content;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed($"Cannot read file: {ex.Message}"));
        }

        if (!content.Contains("FilterCurve", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Failed(
                "This file does not look like a FilterCurve/equalizer export (no 'FilterCurve' marker found)."));

        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in KeyValueRegex.Matches(content))
            pairs[m.Groups["key"].Value.ToLowerInvariant()] = m.Groups["value"].Value;

        var bands = new List<(double Freq, double Gain)>();
        for (int i = 0; ; i++)
        {
            if (!pairs.TryGetValue("f" + i, out var fRaw)) break;
            if (!pairs.TryGetValue("v" + i, out var vRaw)) break;
            if (!TryParse(fRaw, out var freq) || !TryParse(vRaw, out var gain)) break;
            bands.Add((freq, gain));
        }

        if (bands.Count == 0)
            return Task.FromResult(ToolResult.Failed(
                "Could not parse any frequency band (fN=\"...\" vN=\"...\") from the file."));

        return Task.FromResult(ToolResult.Succeeded(Analyze(path, bands, pairs)));
    }

    private static string Analyze(string path, List<(double Freq, double Gain)> bands, Dictionary<string, string> pairs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Analyse de la courbe d'égaliseur : {path}");
        sb.AppendLine();

        var max = bands[0]; var min = bands[0];
        foreach (var b in bands)
        {
            if (b.Gain > max.Gain) max = b;
            if (b.Gain < min.Gain) min = b;
        }

        sb.AppendLine($"Format : FilterCurve (export Wavelet/Equalizer).");
        sb.AppendLine($"- {bands.Count} bandes de fréquences, de {Fmt(bands[0].Freq)} Hz à {Fmt(bands[^1].Freq)} Hz (étalement logarithmique).");
        if (pairs.TryGetValue("filterlength", out var fl))
            sb.AppendLine($"- Longueur du filtre (FilterLength) : {fl} coefficients (nombre de coefficients du filtre, PAS une durée ni des octaves).");
        if (pairs.TryGetValue("interpolationmethod", out var im))
            sb.AppendLine($"- Méthode d'interpolation : {im}.");
        sb.AppendLine();

        sb.AppendLine($"Gains (en dB) :");
        sb.AppendLine($"- Gain maximal : {Fmt(max.Gain)} dB à {Fmt(max.Freq)} Hz.");
        sb.AppendLine($"- Gain minimal : {Fmt(min.Gain)} dB (à {Fmt(min.Freq)} Hz).");
        sb.AppendLine($"- Gain à 10 Hz (sub-bass) : {GainAt(bands, 10)} dB | à 50 Hz : {GainAt(bands, 50)} dB | à 100 Hz : {GainAt(bands, 100)} dB | à 500 Hz : {GainAt(bands, 500)} dB | à 1 kHz : {GainAt(bands, 1000)} dB | à 5 kHz : {GainAt(bands, 5000)} dB | à 10 kHz : {GainAt(bands, 10000)} dB.");
        sb.AppendLine();

        // Plage où le gain dépasse les seuils.
        sb.AppendLine($"Plage du boost (gain supérieur aux seuils) :");
        sb.AppendLine($"- Gain > 0,5 dB : {Range(bands, 0.5)}.");
        sb.AppendLine($"- Gain > 1 dB : {Range(bands, 1.0)}.");
        sb.AppendLine($"- Gain > 3 dB : {Range(bands, 3.0)}.");
        sb.AppendLine();

        // Détermination du type : moyenne des graves vs aiguës.
        var lowCount = Math.Min(5, bands.Count / 2);
        var highCount = Math.Min(5, bands.Count / 2);
        var avgLow = bands.Take(lowCount).Average(b => b.Gain);
        var avgHigh = bands.TakeLast(highCount).Average(b => b.Gain);

        if (avgLow > avgHigh + 1)
            sb.AppendLine($"Conclusion : BOOST DES BASSES. Les {lowCount} bandes les plus graves ont un gain moyen de {Fmt(avgLow)} dB contre {Fmt(avgHigh)} dB pour les {highCount} bandes les plus aiguës : l'action porte sur les graves, PAS sur les aiguës.");
        else if (avgHigh > avgLow + 1)
            sb.AppendLine($"Conclusion : BOOST DES AIGUËS. Les {highCount} bandes les plus aiguës ont un gain moyen de {Fmt(avgHigh)} dB contre {Fmt(avgLow)} dB pour les {lowCount} bandes les plus graves : l'action porte sur les aiguës, PAS sur les graves.");
        else
            sb.AppendLine($"Conclusion : courbe globalement plate (graves : {Fmt(avgLow)} dB de moyenne, aiguës : {Fmt(avgHigh)} dB de moyenne).");

        return sb.ToString();
    }

    private static string Range(List<(double Freq, double Gain)> bands, double threshold)
    {
        var above = bands.Where(b => b.Gain >= threshold).Select(b => b.Freq).ToList();
        if (above.Count == 0)
            return "aucune bande";
        return $"{Fmt(above[0])} Hz à {Fmt(above[^1])} Hz";
    }

    private static string GainAt(List<(double Freq, double Gain)> bands, double targetFreq)
    {
        var nearest = bands.OrderBy(b => Math.Abs(b.Freq - targetFreq)).First();
        return Fmt(nearest.Gain);
    }

    private static string Fmt(double value)
        => value.ToString(value >= 100 ? "0.#" : "0.##", CultureInfo.InvariantCulture);

    private static bool TryParse(string raw, out double value)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
