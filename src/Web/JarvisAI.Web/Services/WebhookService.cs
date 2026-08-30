using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public interface IWebhookService
{
    IReadOnlyList<WebhookConfig> GetWebhooks();
    string AddWebhook(string name, string url, string? secret = null, IReadOnlyList<string>? events = null);
    void RemoveWebhook(string webhookId);
    void EnableWebhook(string webhookId);
    void DisableWebhook(string webhookId);
    Task<WebhookDeliveryResult> DeliverAsync(string eventName, object payload, CancellationToken ct = default);
    IReadOnlyList<WebhookDelivery> GetDeliveryHistory(string? webhookId = null, int maxCount = 50);
}

public sealed class WebhookService : IWebhookService
{
    private readonly ILogger<WebhookService> _logger;
    private readonly string _storagePath;
    private readonly List<WebhookConfig> _webhooks = new();
    private readonly List<WebhookDelivery> _deliveries = new();
    private readonly HttpClient _httpClient;
    private const int MaxDeliveries = 500;

    public WebhookService(ILogger<WebhookService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "webhooks.json");
        Load();
    }

    public IReadOnlyList<WebhookConfig> GetWebhooks() => _webhooks.ToList();

    public string AddWebhook(string name, string url, string? secret = null, IReadOnlyList<string>? events = null)
    {
        var webhook = new WebhookConfig
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Url = url,
            Secret = secret,
            Events = events?.ToList() ?? new List<string> { "*" },
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _webhooks.Add(webhook);
        Save();
        _logger.LogInformation("[Webhook] Added: {Name} → {Url}", name, url);
        return webhook.Id;
    }

    public void RemoveWebhook(string webhookId)
    {
        _webhooks.RemoveAll(w => w.Id == webhookId);
        Save();
    }

    public void EnableWebhook(string webhookId)
    {
        var webhook = _webhooks.FirstOrDefault(w => w.Id == webhookId);
        if (webhook is not null) { webhook.IsEnabled = true; Save(); }
    }

    public void DisableWebhook(string webhookId)
    {
        var webhook = _webhooks.FirstOrDefault(w => w.Id == webhookId);
        if (webhook is not null) { webhook.IsEnabled = false; Save(); }
    }

    public async Task<WebhookDeliveryResult> DeliverAsync(string eventName, object payload, CancellationToken ct = default)
    {
        var result = new WebhookDeliveryResult { EventName = eventName };
        var matchingWebhooks = _webhooks.Where(w =>
            w.IsEnabled && (w.Events.Contains("*") || w.Events.Contains(eventName)));

        var json = JsonSerializer.Serialize(new { event_name = eventName, payload, timestamp = DateTime.UtcNow });

        foreach (var webhook in matchingWebhooks)
        {
            try
            {
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(webhook.Secret))
                    content.Headers.Add("X-Webhook-Secret", webhook.Secret);
                content.Headers.Add("X-Webhook-Event", eventName);

                var response = await _httpClient.PostAsync(webhook.Url, content, ct);

                var delivery = new WebhookDelivery
                {
                    WebhookId = webhook.Id,
                    WebhookName = webhook.Name,
                    EventName = eventName,
                    Success = response.IsSuccessStatusCode,
                    StatusCode = (int)response.StatusCode,
                    Timestamp = DateTime.UtcNow
                };

                lock (_deliveries)
                {
                    _deliveries.Add(delivery);
                    if (_deliveries.Count > MaxDeliveries)
                        _deliveries.RemoveAt(0);
                }

                result.Deliveries.Add(delivery);
                _logger.LogDebug("[Webhook] Delivered to {Name}: {Status}", webhook.Name, response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Webhook] Delivery failed to {Name}", webhook.Name);
                result.Deliveries.Add(new WebhookDelivery
                {
                    WebhookId = webhook.Id,
                    WebhookName = webhook.Name,
                    EventName = eventName,
                    Success = false,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        return result;
    }

    public IReadOnlyList<WebhookDelivery> GetDeliveryHistory(string? webhookId = null, int maxCount = 50)
    {
        lock (_deliveries)
        {
            var query = webhookId is not null
                ? _deliveries.Where(d => d.WebhookId == webhookId)
                : _deliveries.AsEnumerable();

            return query.OrderByDescending(d => d.Timestamp).Take(maxCount).ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<WebhookConfig>>(json);
                if (loaded is not null) _webhooks.AddRange(loaded);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_webhooks, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class WebhookConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Secret { get; set; }
    public List<string> Events { get; set; } = new();
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class WebhookDelivery
{
    public string WebhookId { get; set; } = "";
    public string WebhookName { get; set; } = "";
    public string EventName { get; set; } = "";
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class WebhookDeliveryResult
{
    public string EventName { get; set; } = "";
    public List<WebhookDelivery> Deliveries { get; set; } = new();
}
