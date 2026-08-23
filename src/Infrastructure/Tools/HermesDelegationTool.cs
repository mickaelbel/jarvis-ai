using JarvisAI.Application.Agents;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Integrations;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Tools;

public sealed class HermesDelegationTool : ITool
{
    private readonly IntegrationsStore _store;
    private readonly ILogger<HermesDelegationTool> _logger;
    private readonly HttpClient _http;

    public HermesDelegationTool(IntegrationsStore store, ILogger<HermesDelegationTool> logger)
    {
        _store = store;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    public string Name => "hermes";
    public string Description =>
        "Délégation à Hermes (agent délibératif local) : confie une tâche de réflexion/recherche de fond. " +
        "Doctrine : Jarvis tient les clés et le corps, Hermes pense. " +
        "Action : delegate (tâche en langage naturel). Réponse = résumé vocal + archive complète.";
    public string Category => "ai";
    public SecurityRiskLevel RiskLevel => SecurityRiskLevel.High;
    public string? WaitingPhrase => "Je confie la réflexion à Hermes…";

    public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>
    {
        new("action", "delegate", typeof(string), required: true),
        new("task", "Description complète de la tâche à déléguer", typeof(string), required: true),
        new("intro", "Phrase d'intro vocale (ex: \"Je demande à Hermes d'analyser…\")", typeof(string))
    };

    public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        parameters.TryGetValue("action", out var action);
        parameters.TryGetValue("task", out var task);
        parameters.TryGetValue("intro", out var intro);
        var settings = _store.Get().Hermes;

        if (string.IsNullOrWhiteSpace(settings.ApiUrl) || string.IsNullOrWhiteSpace(settings.ApiKey))
            return ToolResult.Failed("Hermes non configuré : ApiUrl et ApiKey requis dans les paramètres.");
        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Failed("Paramètre task requis.");

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = "hermes-agent",
                input = task,
                conversation = settings.Session
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiUrl.TrimEnd('/')}/v1/responses");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
            req.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

            _logger.LogInformation("[Hermes] Délégation : {Task}", task[..Math.Min(100, task.Length)]);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return ToolResult.Failed($"Hermes error ({resp.StatusCode}) : {body}");

            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var output = doc.RootElement.TryGetProperty("output", out var o) ? o : default;
            var fullText = new System.Text.StringBuilder();
            var resume = "";

            if (output.ValueKind != JsonValueKind.Undefined)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "message")
                    {
                        if (item.TryGetProperty("content", out var c))
                        {
                            foreach (var block in c.EnumerateArray())
                            {
                                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "output_text")
                                {
                                    var txt = block.GetProperty("text").GetString() ?? "";
                                    fullText.AppendLine(txt);
                                    // Extract RESUME: line (accent tolerant)
                                    foreach (var line in txt.Split('\n'))
                                    {
                                        var tl = line.Trim().ToLowerInvariant();
                                        if (tl.StartsWith("resume:") || tl.StartsWith("résumé:"))
                                        {
                                            resume = line.Substring(line.IndexOf(':') + 1).Trim();
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(resume))
                resume = fullText.ToString().Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "Hermes a répondu.";

            // Archive
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JarvisAI", "hermes");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"delegation-{DateTime.Now:yyyyMMdd-HHmmss}.md");
            await File.WriteAllTextAsync(logPath, $"# Délégation Hermes — {DateTime.Now}\n\n**Tâche :** {task}\n\n**Réponse complète :**\n{fullText}", ct);

            var spoken = string.IsNullOrWhiteSpace(intro) ? resume : $"{intro} {resume}";
            return ToolResult.Succeeded(spoken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hermes] Délégation échouée");
            return ToolResult.Failed($"Erreur Hermes : {ex.Message}");
        }
    }
}