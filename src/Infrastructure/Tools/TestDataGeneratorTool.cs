using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisAI.Infrastructure.Tools;

public sealed class TestDataGeneratorTool : ITool
{
    private readonly ILogger<TestDataGeneratorTool> _logger;
    private readonly Random _random = new();

    public string Name => "test_data_generator";
    public string Description => "Générer des données de test réalistes (noms, emails, adresses, montants, dates)";
    public string Category => "Development";
    public bool IsReadOnly => false;

    public TestDataGeneratorTool(ILogger<TestDataGeneratorTool> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter("type", "Type de données: name, email, phone, address, amount, date, boolean, text, json, csv", typeof(string), required: true),
        new ToolParameter("count", "Nombre d'entrées à générer (défaut: 10)", typeof(string)),
        new ToolParameter("format", "Format spécifique (pour date: yyyy-MM-dd, pour amount: currency)", typeof(string)),
        new ToolParameter("locale", "Localisation: fr, en, de (défaut: fr)", typeof(string)),
        new ToolParameter("constraints", "Contraintes: min, max, pattern (ex: min=1;max=100)", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var type = parameters.GetValueOrDefault("type") ?? "text";
        var count = int.TryParse(parameters.GetValueOrDefault("count"), out var c) ? Math.Min(c, 1000) : 10;
        var format = parameters.GetValueOrDefault("format");
        var locale = parameters.GetValueOrDefault("locale") ?? "fr";
        var constraints = ParseConstraints(parameters.GetValueOrDefault("constraints"));

        try
        {
            var data = type.ToLowerInvariant() switch
            {
                "name" => GenerateNames(count, locale),
                "email" => GenerateEmails(count, locale),
                "phone" => GeneratePhones(count, locale),
                "address" => GenerateAddresses(count, locale),
                "amount" => GenerateAmounts(count, constraints),
                "date" => GenerateDates(count, format),
                "boolean" => GenerateBooleans(count),
                "text" => GenerateTexts(count),
                "json" => GenerateJson(count),
                "csv" => GenerateCsv(count),
                _ => GenerateTexts(count)
            };

            return ToolResult.Succeeded(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TestDataGen] Error");
            return ToolResult.Failed($"Erreur: {ex.Message}");
        }
    }

    private Dictionary<string, string> ParseConstraints(string? constraints)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(constraints)) return result;

        foreach (var part in constraints.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
                result[kv[0].Trim()] = kv[1].Trim();
        }
        return result;
    }

    private string GenerateNames(int count, string locale)
    {
        var firstNames = locale == "fr"
            ? new[] { "Jean", "Marie", "Pierre", "Sophie", "Lucas", "Emma", "Louis", "Léa", "Hugo", "Chloé", "Nathan", "Manon", "Raphaël", "Juliette", "Gabin" }
            : new[] { "James", "Mary", "Robert", "Patricia", "John", "Jennifer", "Michael", "Linda", "David", "Elizabeth", "William", "Barbara", "Richard", "Susan", "Joseph" };

        var lastNames = locale == "fr"
            ? new[] { "Dupont", "Martin", "Bernard", "Dubois", "Thomas", "Robert", "Richard", "Petit", "Durand", "Moreau", "Laurent", "Simon", "Michel", "Lefevre", "Leroy" }
            : new[] { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez", "Anderson", "Taylor", "Thomas", "Hernandez", "Moore" };

        var sb = new StringBuilder();
        sb.AppendLine($"Noms générés ({count}):");
        for (int i = 0; i < count; i++)
        {
            var first = firstNames[_random.Next(firstNames.Length)];
            var last = lastNames[_random.Next(lastNames.Length)];
            sb.AppendLine($"  {first} {last}");
        }
        return sb.ToString();
    }

    private string GenerateEmails(int count, string locale)
    {
        var domains = locale == "fr"
            ? new[] { "gmail.com", "yahoo.fr", "hotmail.fr", "outlook.fr", "free.fr", "wanadoo.fr" }
            : new[] { "gmail.com", "yahoo.com", "hotmail.com", "outlook.com", "protonmail.com" };

        var sb = new StringBuilder();
        sb.AppendLine($"Emails générés ({count}):");
        for (int i = 0; i < count; i++)
        {
            var name = $"user{_random.Next(1000, 9999)}";
            var domain = domains[_random.Next(domains.Length)];
            sb.AppendLine($"  {name}@{domain}");
        }
        return sb.ToString();
    }

    private string GeneratePhones(int count, string locale)
    {
        var prefix = locale == "fr" ? "+33" : "+1";
        var sb = new StringBuilder();
        sb.AppendLine($"Téléphones générés ({count}):");
        for (int i = 0; i < count; i++)
        {
            if (locale == "fr")
                sb.AppendLine($"  {prefix} 6{_random.Next(10, 99)}{_random.Next(100000, 999999)}");
            else
                sb.AppendLine($"  {prefix} ({_random.Next(200, 999)}) {_random.Next(200, 999)}-{_random.Next(1000, 9999)}");
        }
        return sb.ToString();
    }

    private string GenerateAddresses(int count, string locale)
    {
        var streets = locale == "fr"
            ? new[] { "Rue de la Paix", "Boulevard Saint-Germain", "Avenue des Champs-Élysées", "Rue de Rivoli", "Place Bellecour", "Rue National" }
            : new[] { "Main Street", "Oak Avenue", "Park Boulevard", "Elm Street", "Maple Drive", "Cedar Lane" };

        var cities = locale == "fr"
            ? new[] { "Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes" }
            : new[] { "New York", "Los Angeles", "Chicago", "Houston", "Phoenix", "Philadelphia" };

        var sb = new StringBuilder();
        sb.AppendLine($"Adresses générées ({count}):");
        for (int i = 0; i < count; i++)
        {
            var street = streets[_random.Next(streets.Length)];
            var num = _random.Next(1, 200);
            var city = cities[_random.Next(cities.Length)];
            var zip = locale == "fr" ? $"{_random.Next(10000, 99999)}" : $"{_random.Next(10000, 99999)}";
            sb.AppendLine($"  {num} {street}, {zip} {city}");
        }
        return sb.ToString();
    }

    private string GenerateAmounts(int count, Dictionary<string, string> constraints)
    {
        var min = constraints.TryGetValue("min", out var minStr) && decimal.TryParse(minStr, out var m) ? m : 0;
        var max = constraints.TryGetValue("max", out var maxStr) && decimal.TryParse(maxStr, out var mx) ? mx : 10000;

        var sb = new StringBuilder();
        sb.AppendLine($"Montants générés ({count}):");
        for (int i = 0; i < count; i++)
        {
            var amount = (decimal)(_random.NextDouble() * (double)(max - min) + (double)min);
            sb.AppendLine($"  {amount:N2} €");
        }
        return sb.ToString();
    }

    private string GenerateDates(int count, string? format)
    {
        var fmt = format ?? "yyyy-MM-dd";
        var sb = new StringBuilder();
        sb.AppendLine($"Dates générées ({count}):");
        for (int i = 0; i < count; i++)
        {
            var date = DateTime.UtcNow.AddDays(-_random.Next(0, 365));
            sb.AppendLine($"  {date.ToString(fmt)}");
        }
        return sb.ToString();
    }

    private string GenerateBooleans(int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Booléens générés ({count}):");
        for (int i = 0; i < count; i++)
            sb.AppendLine($"  {_random.Next(2) == 0}");
        return sb.ToString();
    }

    private string GenerateTexts(int count)
    {
        var words = new[] { "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor", "incididunt", "ut", "labore" };

        var sb = new StringBuilder();
        sb.AppendLine($"Textes générés ({count}):");
        for (int i = 0; i < count; i++)
        {
            var wordCount = _random.Next(5, 15);
            var text = string.Join(" ", Enumerable.Range(0, wordCount).Select(_ => words[_random.Next(words.Length)]));
            sb.AppendLine($"  {char.ToUpper(text[0])}{text[1..]}.");
        }
        return sb.ToString();
    }

    private string GenerateJson(int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[");
        for (int i = 0; i < count; i++)
        {
            var comma = i < count - 1 ? "," : "";
            sb.AppendLine($"  {{\"id\": {i + 1}, \"name\": \"User {i + 1}\", \"email\": \"user{i + 1}@example.com\", \"active\": {_random.Next(2) == 0}}}{comma}");
        }
        sb.AppendLine("]");
        return sb.ToString();
    }

    private string GenerateCsv(int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,name,email,active");
        for (int i = 0; i < count; i++)
            sb.AppendLine($"{i + 1},User {i + 1},user{i + 1}@example.com,{_random.Next(2) == 0}");
        return sb.ToString();
    }
}
