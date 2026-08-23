using System.Collections.Concurrent;
using JarvisAI.Application.AI;
using JarvisAI.Infrastructure.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace JarvisAI.Web.Services;

public sealed record OllamaKeepAliveState(
    IReadOnlyList<string> WarmModels,
    IReadOnlyList<string> PendingModels,
    bool IdleUnloadEnabled,
    TimeSpan IdleUnloadTimeout);

public sealed class OllamaKeepAliveService : BackgroundService
{
    private readonly IModelRouter _router;
    private readonly ILogger<OllamaKeepAliveService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string[] _extraModels;
    private readonly OllamaRunMonitor _monitor;
    private readonly OllamaModelService? _modelService;
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _refreshDelay = TimeSpan.FromMinutes(4);
    private readonly TimeSpan _idleUnloadTimeout;
    private readonly TimeSpan _idleUnloadCheckInterval = TimeSpan.FromMinutes(1);
    private readonly bool _idleUnloadEnabled;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastActivity = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly HashSet<string> _warm = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private DateTimeOffset _lastUnloadCheck = DateTimeOffset.MinValue;

    public OllamaKeepAliveService(
        HttpClient httpClient,
        IModelRouter router,
        ILogger<OllamaKeepAliveService> logger,
        string[]? extraModels = null,
        OllamaRunMonitor? monitor = null,
        OllamaModelService? modelService = null,
        TimeSpan? idleUnloadTimeout = null,
        bool idleUnloadEnabled = true)
    {
        _httpClient = httpClient;
        _router = router;
        _logger = logger;
        _extraModels = extraModels ?? Array.Empty<string>();
        _monitor = monitor ?? new OllamaRunMonitor();
        _modelService = modelService;
        _idleUnloadTimeout = idleUnloadTimeout ?? TimeSpan.FromMinutes(15);
        _idleUnloadEnabled = idleUnloadEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var model in GetModels())
            _pending.Add(model);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var model in Snapshot(_pending))
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (await KeepAliveAsync(model, stoppingToken))
                {
                    MarkUsed(model);
                    lock (_sync)
                    {
                        _warm.Add(model);
                        _pending.Remove(model);
                    }
                }
            }

            // Refresh warm models periodically so their keep_alive window never expires
            foreach (var model in Snapshot(_warm))
            {
                if (stoppingToken.IsCancellationRequested) return;
                await KeepAliveAsync(model, stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) return;

            var now = DateTimeOffset.UtcNow;
            if (now - _lastUnloadCheck >= _idleUnloadCheckInterval)
            {
                await UnloadIdleModelsAsync(now, stoppingToken);
                _lastUnloadCheck = DateTimeOffset.UtcNow;
            }

            bool anyPending;
            lock (_sync) anyPending = _pending.Count > 0;

            await Task.Delay(anyPending ? _retryDelay : _refreshDelay, stoppingToken);
        }
    }

    public async Task<bool> PreloadAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (await KeepAliveAsync(model, ct))
        {
            MarkUsed(model);
            lock (_sync)
            {
                _warm.Add(model);
                _pending.Remove(model);
            }
            return true;
        }
        return false;
    }

    public OllamaKeepAliveState GetState()
    {
        lock (_sync)
        {
            return new OllamaKeepAliveState(
                _warm.OrderBy(x => x).ToList(),
                _pending.OrderBy(x => x).ToList(),
                _idleUnloadEnabled,
                _idleUnloadTimeout);
        }
    }

    internal async Task<IReadOnlyList<string>> UnloadIdleModelsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var unloaded = new List<string>();
        if (!_idleUnloadEnabled || _modelService is null) return unloaded;

        var running = await _modelService.GetRunningModelsAsync(ct);
        if (running.Count == 0) return unloaded;

        foreach (var run in _monitor.Recent)
        {
            if (!string.IsNullOrWhiteSpace(run.Model))
                _lastActivity[run.Model] = run.OccurredAt;
        }

        foreach (var process in running)
        {
            if (string.IsNullOrWhiteSpace(process.Name)) continue;

            var lastActivity = GetLastActivity(process.Name);
            var idle = lastActivity is null ? TimeSpan.Zero : now - lastActivity.Value;
            if (idle <= _idleUnloadTimeout) continue;

            if (await UnloadAsync(process.Name, ct))
            {
                unloaded.Add(process.Name);
                lock (_sync)
                {
                    _warm.Remove(process.Name);
                    _pending.Remove(process.Name);
                }
            }
        }

        return unloaded;
    }

    internal void MarkUsed(string model, DateTimeOffset? at = null)
        => _lastActivity[model] = at ?? DateTimeOffset.UtcNow;

    internal DateTimeOffset? GetLastActivity(string model)
        => _lastActivity.TryGetValue(model, out var t) ? t : null;

    private IEnumerable<string> GetModels()
    {
        var options = _router.Options;
        var models = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.FastModel)) models.Add(options.FastModel);
        if (!string.IsNullOrWhiteSpace(options.ReasoningModel) && options.ReasoningModel != options.FastModel)
            models.Add(options.ReasoningModel);
        foreach (var extra in _extraModels)
        {
            if (!string.IsNullOrWhiteSpace(extra) && !models.Contains(extra)) models.Add(extra);
        }
        return models;
    }

    private List<string> Snapshot(HashSet<string> set)
    {
        lock (_sync) return set.ToList();
    }

    private async Task<bool> KeepAliveAsync(string model, CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["prompt"] = "",
                ["stream"] = false,
                ["keep_alive"] = _router.Options.KeepAlive
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("/api/generate", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[KeepAlive] Preload of {Model} returned HTTP {StatusCode}", model, (int)response.StatusCode);
                return false;
            }
            _logger.LogInformation("[KeepAlive] Model {Model} preloaded with keep_alive={KeepAlive}", model, _router.Options.KeepAlive);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[KeepAlive] Ollama not ready yet for {Model}", model);
            return false;
        }
    }

    private async Task<bool> UnloadAsync(string model, CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["prompt"] = "",
                ["stream"] = false,
                ["keep_alive"] = 0
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("/api/generate", content, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[KeepAlive] Model {Model} unloaded after idle", model);
                return true;
            }
            _logger.LogWarning("[KeepAlive] Unload of {Model} returned HTTP {StatusCode}", model, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[KeepAlive] Unload of {Model} failed", model);
            return false;
        }
    }
}
