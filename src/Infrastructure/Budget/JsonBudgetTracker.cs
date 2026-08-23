using JarvisAI.Application.Budget;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarvisAI.Infrastructure.Budget;

/// <summary>
/// Suivi des coûts LLM : journal JSON %LOCALAPPDATA%\JarvisAI\budget.json,
/// totaux jour/mois, alerte idempotente à N % du plafond (une fois par jour),
/// bascule optionnelle vers un modèle local au plafond. Thread-safe.
/// </summary>
public sealed class JsonBudgetTracker : IBudgetTracker
{
    private readonly BudgetOptions _options;
    private readonly ILogger<JsonBudgetTracker> _logger;
    private readonly object _lock = new();
    private readonly string _filePath;

    private BudgetFile _file = new();
    private bool _alertDaySignaled;
    private bool _alertMonthSignaled;
    private bool _capSwitchedToLocal;

    public JsonBudgetTracker(BudgetOptions options, ILogger<JsonBudgetTracker> logger, string? filePath = null)
    {
        _options = options;
        _logger = logger;
        var dir = filePath is not null
            ? Path.GetDirectoryName(filePath)!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAI");
        Directory.CreateDirectory(dir);
        _filePath = filePath ?? Path.Combine(dir, "budget.json");
        Load();
    }

    public Task RecordAsync(string model, int promptTokens, int completionTokens, CancellationToken cancellationToken = default)
    {
        if (promptTokens <= 0 && completionTokens <= 0) return Task.CompletedTask;

        var price = ResolvePrice(model);
        var cost = price is null ? 0m
            : promptTokens * price.Value.Input / 1_000_000m + completionTokens * price.Value.Output / 1_000_000m;

        if (price is null)
        {
            // Modèle gratuit/local : on compte les tokens mais pas de coût.
            lock (_lock)
            {
                AddTokens(model, promptTokens, completionTokens);
                SaveLocked();
            }
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            RollDayIfNeeded();
            var day = GetOrCreateDay();
            day.TotalUsd += cost;
            AddTokens(model, promptTokens, completionTokens, day);

            var state = ComputeState();

            if (_options.DailyCapUsd is > 0 && state.PercentOfDay >= _options.AlertPercent && state.PercentOfDay < 100 && !_alertDaySignaled)
                _alertDaySignaled = true;
            if (_options.MonthlyCapUsd is > 0 && state.PercentOfMonth >= _options.AlertPercent && state.PercentOfMonth < 100 && !_alertMonthSignaled)
                _alertMonthSignaled = true;
            if (state.CapExceeded && !_capSwitchedToLocal)
                _capSwitchedToLocal = true;

            SaveLocked();

            _logger.LogInformation("[Budget] {Model}: {In}/{Out} tokens = {Cost:F4}$ ({Day:F4}$/jour, {Month:F4}$/mois)",
                model, promptTokens, completionTokens, cost, state.TotalDayUsd, state.TotalMonthUsd);

            if (_alertDaySignaled || _alertMonthSignaled)
                _logger.LogWarning("[Budget] ALERTE seuil {Pct}% atteint", _options.AlertPercent);
            if (_capSwitchedToLocal && _options.AutoSwitchToLocalAtCap)
                _logger.LogWarning("[Budget] PLAFOND DÉPASSÉ — bascule vers le modèle local '{Model}'", _options.LocalFallbackModel);
        }

        return Task.CompletedTask;
    }

    public Task<BudgetState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock) return Task.FromResult(ComputeState());
    }

    public string FormatSummary()
    {
        lock (_lock)
        {
            var s = ComputeState();
            var dayCap = _options.DailyCapUsd is > 0 ? $" sur {_options.DailyCapUsd:F2}$ ({s.PercentOfDay:F0}%)" : "";
            var monthCap = _options.MonthlyCapUsd is > 0 ? $" sur {_options.MonthlyCapUsd:F2}$ ({s.PercentOfMonth:F0}%)" : "";
            return $"Dépenses IA aujourd'hui : {s.TotalDayUsd:F4}${dayCap}, ce mois : {s.TotalMonthUsd:F4}${monthCap}. ACTION TERMINÉE.";
        }
    }

    public string ApplyFallback(string requestedModel)
    {
        lock (_lock)
        {
            var s = ComputeState();
            if (!s.CapExceeded || !_options.AutoSwitchToLocalAtCap) return requestedModel;
            if (string.Equals(requestedModel, _options.LocalFallbackModel, StringComparison.OrdinalIgnoreCase))
                return requestedModel;
            _logger.LogWarning("[Budget] Plafond dépassé : '{Requested}' remplacé par le modèle local '{Local}'",
                requestedModel, _options.LocalFallbackModel);
            return _options.LocalFallbackModel;
        }
    }

    private (decimal Input, decimal Output)? ResolvePrice(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        (string Key, decimal Input, decimal Output)? best = null;
        foreach (var kv in _options.Prices)
        {
            if (!model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (best is null || kv.Key.Length > best.Value.Key.Length)
                best = (kv.Key, kv.Value.InputPerMillion, kv.Value.OutputPerMillion);
        }
        return best is null ? null : (best.Value.Input, best.Value.Output);
    }

    private BudgetState ComputeState()
    {
        var totalDay = _file.Days.TryGetValue(TodayKey(), out var d) ? d.TotalUsd : 0m;
        decimal? pctDay = _options.DailyCapUsd is > 0 ? totalDay / _options.DailyCapUsd!.Value * 100m : null;
        decimal? pctMonth = _options.MonthlyCapUsd is > 0 ? _file.MonthTotalUsd / _options.MonthlyCapUsd!.Value * 100m : null;
        bool exceeded = (pctDay is not null && pctDay >= 100m) || (pctMonth is not null && pctMonth >= 100m);
        return new BudgetState(totalDay, _file.MonthTotalUsd, _options.DailyCapUsd, _options.MonthlyCapUsd,
            pctDay >= 80m, pctMonth >= 80m, exceeded);
    }

    private void AddTokens(string model, int promptTokens, int completionTokens, DayEntry? day = null)
    {
        day ??= GetOrCreateDay();
        day.TotalPromptTokens += promptTokens;
        day.TotalCompletionTokens += completionTokens;
        _file.Models[model] = _file.Models.TryGetValue(model, out var m) ? m + promptTokens + completionTokens : promptTokens + completionTokens;
    }

    private DayEntry GetOrCreateDay()
    {
        RollDayIfNeeded();
        var key = TodayKey();
        if (!_file.Days.TryGetValue(key, out var day))
        {
            day = new DayEntry();
            _file.Days[key] = day;
        }
        return day;
    }

    private void RollDayIfNeeded()
    {
        var today = TodayKey();
        if (string.Equals(_file.CurrentDay, today, StringComparison.Ordinal)) return;

        var isNewMonth = !today.StartsWith(_file.CurrentMonth ?? string.Empty, StringComparison.Ordinal);
        _file.CurrentDay = today;
        _file.CurrentMonth = today[..7];
        if (isNewMonth)
            _file.MonthTotalUsd = 0m;
        _alertDaySignaled = false;
        _capSwitchedToLocal = false;
        _logger.LogInformation("[Budget] Nouveau jour ({Day}) — alertes réarmées", today);
    }

    private static string TodayKey() => DateTime.UtcNow.Date.ToString("yyyy-MM-dd");

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                _file = JsonSerializer.Deserialize<BudgetFile>(File.ReadAllText(_filePath)) ?? new BudgetFile();
            _file.CurrentDay ??= TodayKey();
            _file.CurrentMonth ??= TodayKey()[..7];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Budget] Lecture impossible de {File}, compteur remis à zéro", _filePath);
            _file = new BudgetFile();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath + ".tmp", json);
            File.Move(_filePath + ".tmp", _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Budget] Écriture impossible de {File}", _filePath);
        }
    }

    private sealed class BudgetFile
    {
        [JsonPropertyName("current_day")] public string? CurrentDay { get; set; }
        [JsonPropertyName("current_month")] public string? CurrentMonth { get; set; }
        [JsonPropertyName("month_total_usd")] public decimal MonthTotalUsd { get; set; }
        [JsonPropertyName("days")] public Dictionary<string, DayEntry> Days { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("models_tokens")] public Dictionary<string, long> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DayEntry
    {
        [JsonPropertyName("total_usd")] public decimal TotalUsd { get; set; }
        [JsonPropertyName("prompt_tokens")] public long TotalPromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public long TotalCompletionTokens { get; set; }
    }
}
