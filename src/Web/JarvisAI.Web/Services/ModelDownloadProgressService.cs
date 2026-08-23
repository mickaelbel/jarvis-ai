using JarvisAI.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Web.Services;

/// <summary>
/// Mutable snapshot of one model download, updated live as the pull streams
/// progress chunks from Ollama. Exposed over SignalR to all connected clients.
/// </summary>
public sealed class ModelDownloadState
{
    public string Model { get; init; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double Percent { get; set; }
    public long CompletedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public TimeSpan? Eta { get; set; }
    public bool IsActive { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Formatted download size, e.g. "1.2 GB".</summary>
    public string SizeDisplay => ModelDownloadProgressService.FormatBytes(CompletedBytes);

    /// <summary>Formatted download speed, e.g. "45.3 MB/s".</summary>
    public string SpeedDisplay => ModelDownloadProgressService.FormatSpeed(BytesPerSecond);

    /// <summary>Human-readable ETA, e.g. "2m 05s" or "—".</summary>
    public string EtaDisplay => Eta is { } e ? ModelDownloadProgressService.FormatEta(e) : "—";

    /// <summary>Unicode progress bar, e.g. "██████░░░░ 60%".</summary>
    public string ProgressBar => ModelDownloadProgressService.BuildProgressBar(Percent);
}

/// <summary>
/// Runs Ollama model pulls in the background and streams progress to every
/// SignalR client (and to in-app listeners) without blocking the UI thread.
/// </summary>
public sealed class ModelDownloadProgressService : IDisposable
{
    private readonly OllamaModelService _ollama;
    private readonly IHubContext<JarvisHub>? _hubContext;
    private readonly ILogger<ModelDownloadProgressService> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, ModelDownloadState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ModelDownloadProgressService(
        OllamaModelService ollama,
        IHubContext<JarvisHub>? hubContext = null,
        ILogger<ModelDownloadProgressService>? logger = null)
    {
        _ollama = ollama;
        _hubContext = hubContext;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelDownloadProgressService>.Instance;
    }

    /// <summary>Raised whenever any download state changes (percent, speed, done...).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Starts an asynchronous pull for the given model. Returns false when a
    /// pull is already running for the same model. The pull itself runs in the
    /// background and never blocks the caller.
    /// </summary>
    public Task<bool> StartPullAsync(string model)
    {
        var name = model.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(false);

        lock (_lock)
        {
            if (_cancellations.ContainsKey(name))
                return Task.FromResult(false);
            _cancellations[name] = new CancellationTokenSource();
        }

        var state = new ModelDownloadState { Model = name, Status = "Starting...", IsActive = true };
        lock (_lock) _states[name] = state;
        RaiseChanged(state);

        _ = Task.Run(() => PullLoopAsync(name, state));
        return Task.FromResult(true);
    }

    public void CancelPull(string model)
    {
        CancellationTokenSource? cts;
        lock (_lock) _cancellations.TryGetValue(model, out cts);
        cts?.Cancel();
    }

    public ModelDownloadState? GetState(string model)
    {
        lock (_lock) return _states.TryGetValue(model, out var s) ? s : null;
    }

    public IReadOnlyList<ModelDownloadState> GetAll()
    {
        lock (_lock) return _states.Values.ToList();
    }

    public IReadOnlyList<ModelDownloadState> GetActive()
    {
        lock (_lock) return _states.Values.Where(s => s.IsActive).ToList();
    }

    private async Task PullLoopAsync(string model, ModelDownloadState state)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CurrentCts(model);
            var progress = new Progress<OllamaPullProgress>(p => UpdateProgress(state, p, stopwatch.ElapsedMilliseconds));
            var results = await _ollama.PullModelAsync(model, progress, cts?.Token ?? CancellationToken.None);

            stopwatch.Stop();

            var cancelled = cts is { IsCancellationRequested: true };
            var succeeded = !cancelled && results.Any(r => r.Status == "success");
            Finish(state, succeeded, succeeded ? "Completed" : null, cancelled ? "Cancelled" : "Model pull failed");
        }
        catch (OperationCanceledException)
        {
            Finish(state, false, null, "Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ModelDownload] Pull failed for {Model}", model);
            Finish(state, false, null, ex.Message);
        }
        finally
        {
            lock (_lock) _cancellations.Remove(model);
        }
    }

    private void UpdateProgress(ModelDownloadState state, OllamaPullProgress p, long elapsedMs)
    {
        if (!state.IsActive) return;
        state.Status = p.Status;
        state.CompletedBytes = p.Completed;
        state.TotalBytes = p.Total;
        state.Percent = ComputePercent(p.Completed, p.Total);
        state.BytesPerSecond = ComputeSpeed(p.Completed, elapsedMs);
        state.Eta = ComputeEta(p.Completed, p.Total, state.BytesPerSecond);
        RaiseChanged(state);
    }

    private void Finish(ModelDownloadState state, bool succeeded, string? successStatus, string? error)
    {
        state.IsActive = false;
        state.Succeeded = succeeded;
        if (succeeded) state.Status = successStatus ?? "Completed";
        else state.Error = error;
        RaiseChanged(state);
    }

    private CancellationTokenSource? CurrentCts(string model)
    {
        lock (_lock) return _cancellations.TryGetValue(model, out var cts) ? cts : null;
    }

    private void RaiseChanged(ModelDownloadState state)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            _hubContext?.Clients.All.SendAsync("ModelDownloadProgress", state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ModelDownload] Failed to broadcast progress");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<CancellationTokenSource> ctsList;
        lock (_lock)
        {
            ctsList = _cancellations.Values.ToList();
            _cancellations.Clear();
        }
        foreach (var cts in ctsList)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ================================================================
    // Pure computation helpers (unit-testable).
    // ================================================================

    public static double ComputePercent(long completed, long total)
        => total > 0 ? Math.Clamp((double)completed / total * 100, 0, 100) : 0;

    public static double ComputeSpeed(long completedBytes, long elapsedMs)
        => elapsedMs > 0 ? (double)completedBytes / elapsedMs * 1000 : 0;

    public static TimeSpan? ComputeEta(long completedBytes, long totalBytes, double bytesPerSecond)
    {
        if (totalBytes <= 0 || bytesPerSecond <= 0)
            return null;
        var remaining = totalBytes - completedBytes;
        if (remaining <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(remaining / bytesPerSecond);
    }

    public static string BuildProgressBar(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var filled = (int)Math.Round(percent / 10);
        return new string('█', filled) + new string('░', 10 - filled);
    }

    public static string FormatBytes(long bytes)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1", culture) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1", culture) + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("F2", culture) + " GB";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (bytesPerSecond < 1024) return bytesPerSecond.ToString("F0", culture) + " B/s";
        if (bytesPerSecond < 1024 * 1024) return (bytesPerSecond / 1024).ToString("F1", culture) + " KB/s";
        if (bytesPerSecond < 1024L * 1024 * 1024) return (bytesPerSecond / (1024.0 * 1024)).ToString("F1", culture) + " MB/s";
        return (bytesPerSecond / (1024.0 * 1024 * 1024)).ToString("F2", culture) + " GB/s";
    }

    public static string FormatEta(TimeSpan eta)
    {
        if (eta < TimeSpan.Zero) return "—";
        if (eta.TotalSeconds < 1) return "<1s";
        if (eta.TotalMinutes < 1) return $"{(int)eta.TotalSeconds}s";
        if (eta.TotalHours < 1) return $"{(int)eta.TotalMinutes}m {(eta.Seconds):00}s";
        return $"{(int)eta.TotalHours}h {(eta.Minutes):00}m";
    }
}
