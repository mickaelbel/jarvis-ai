using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.Automation;

public interface ISubAgentService
{
    Task<SubAgentResult> DelegateTaskAsync(string task, string? model = null, string? systemPrompt = null, CancellationToken ct = default);
    Task<List<string>> GetAvailableModelsAsync();
    Task<SubAgentResult> AskSpecializedAsync(string question, string expertise, CancellationToken ct = default);
}

public sealed class SubAgentService : ISubAgentService
{
    private readonly ILogger<SubAgentService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _ollamaUrl;

    public SubAgentService(ILogger<SubAgentService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ollamaUrl = "http://127.0.0.1:11434";
    }

    public async Task<SubAgentResult> DelegateTaskAsync(string task, string? model = null, string? systemPrompt = null, CancellationToken ct = default)
    {
        var result = new SubAgentResult
        {
            Task = task,
            Model = model ?? "llama3.1:latest",
            StartedAt = DateTime.UtcNow
        };

        var prompt = systemPrompt ?? $"Tu es un agent spécialisé. Exécute cette tâche de manière précise et concise. Réponds UNIQUEMENT avec le résultat, sans blabla.\n\nTâche : {task}";

        try
        {
            var requestBody = new
            {
                model = result.Model,
                messages = new[]
                {
                    new { role = "system", content = prompt },
                    new { role = "user", content = task }
                },
                stream = false,
                options = new
                {
                    num_predict = 2048,
                    temperature = 0.3
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_ollamaUrl}/api/chat", content, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("message", out var message))
            {
                result.Response = message.GetProperty("content").GetString() ?? "";
                result.Success = true;
            }

            result.CompletedAt = DateTime.UtcNow;
            result.Duration = result.CompletedAt.Value - result.StartedAt;

            _logger.LogInformation("[SubAgent] Task completed in {Ms}ms using {Model}",
                result.Duration?.TotalMilliseconds ?? 0, result.Model);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.CompletedAt = DateTime.UtcNow;
            _logger.LogError(ex, "[SubAgent] Failed");
        }

        return result;
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"{_ollamaUrl}/api/tags");
            var doc = JsonDocument.Parse(response);
            var models = new List<string>();

            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                        models.Add(name.GetString() ?? "");
                }
            }

            return models;
        }
        catch
        {
            return new List<string> { "llama3.1:latest" };
        }
    }

    public async Task<SubAgentResult> AskSpecializedAsync(string question, string expertise, CancellationToken ct = default)
    {
        var systemPrompt = $"Tu es un expert en {expertise}. Réponds de manière précise, technique et concise. " +
                          $"Pas de blabla, juste les faits et l'utile. " +
                          $"Si tu n'es pas sûr, dis-le clairement.";

        return await DelegateTaskAsync(question, null, systemPrompt, ct);
    }
}

public sealed class SubAgentResult
{
    public bool Success { get; set; }
    public string Task { get; set; } = "";
    public string Model { get; set; } = "";
    public string Response { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
}
