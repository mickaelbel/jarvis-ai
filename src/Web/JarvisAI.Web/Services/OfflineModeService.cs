using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IOfflineModeService
{
    bool IsOfflineMode { get; }
    bool IsLocalLlmAvailable { get; }
    Task<bool> CheckConnectivityAsync(CancellationToken ct = default);
    Task<string> QueryLocalLlmAsync(string prompt, CancellationToken ct = default);
    void EnableOfflineMode();
    void DisableOfflineMode();
    event EventHandler<bool>? OfflineModeChanged;
}

public sealed class OfflineModeService : IOfflineModeService
{
    private readonly ILogger<OfflineModeService> _logger;
    private readonly HttpClient _httpClient;
    private bool _isOfflineMode;
    private bool _isLocalLlmAvailable;

    public bool IsOfflineMode => _isOfflineMode;
    public bool IsLocalLlmAvailable => _isLocalLlmAvailable;

    public event EventHandler<bool>? OfflineModeChanged;

    public OfflineModeService(ILogger<OfflineModeService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _ = CheckLocalLlmAsync();
    }

    public async Task<bool> CheckConnectivityAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("http://localhost:11434/api/tags", ct);
            _isLocalLlmAvailable = response.IsSuccessStatusCode;
            return _isLocalLlmAvailable;
        }
        catch
        {
            _isLocalLlmAvailable = false;
            return false;
        }
    }

    public async Task<string> QueryLocalLlmAsync(string prompt, CancellationToken ct = default)
    {
        if (!_isLocalLlmAvailable)
            return "LLM local non disponible. Vérifiez qu'Ollama est démarré.";

        try
        {
            var request = new
            {
                model = "qwen3.5:2b",
                prompt = prompt,
                stream = false
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("http://localhost:11434/api/generate", content, ct);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseJson);
                return doc.RootElement.GetProperty("response").GetString() ?? "";
            }

            return "Erreur lors de la requête au LLM local.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OfflineMode] Local LLM query failed");
            return $"Erreur: {ex.Message}";
        }
    }

    public void EnableOfflineMode()
    {
        _isOfflineMode = true;
        OfflineModeChanged?.Invoke(this, true);
        _logger.LogInformation("[OfflineMode] Enabled");
    }

    public void DisableOfflineMode()
    {
        _isOfflineMode = false;
        OfflineModeChanged?.Invoke(this, false);
        _logger.LogInformation("[OfflineMode] Disabled");
    }

    private async Task CheckLocalLlmAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("http://localhost:11434/api/tags");
            _isLocalLlmAvailable = response.IsSuccessStatusCode;
        }
        catch
        {
            _isLocalLlmAvailable = false;
        }
    }
}
