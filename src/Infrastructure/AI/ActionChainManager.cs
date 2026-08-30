using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace JarvisAI.Infrastructure.AI;

public interface IActionChainManager
{
    Task<string> CreateChainAsync(string name, IReadOnlyList<ChainStep> steps, CancellationToken ct = default);
    Task<ChainExecutionResult> ExecuteChainAsync(string chainId, Dictionary<string, string>? parameters = null, CancellationToken ct = default);
    Task<IReadOnlyList<ChainDefinition>> GetChainsAsync(CancellationToken ct = default);
    Task DeleteChainAsync(string chainId, CancellationToken ct = default);
    Task StopChainAsync(string executionId, CancellationToken ct = default);
}

public sealed class ActionChainManager : IActionChainManager
{
    private readonly ILogger<ActionChainManager> _logger;
    private readonly string _storagePath;
    private readonly List<ChainDefinition> _chains = new();
    private readonly Dictionary<string, CancellationTokenSource> _runningExecutions = new();

    public ActionChainManager(ILogger<ActionChainManager> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JarvisAI", "action_chains.json");
        Load();
    }

    public async Task<string> CreateChainAsync(string name, IReadOnlyList<ChainStep> steps, CancellationToken ct = default)
    {
        var chain = new ChainDefinition
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Steps = steps.ToList(),
            CreatedAt = DateTime.UtcNow
        };

        _chains.Add(chain);
        Save();
        _logger.LogInformation("[ActionChain] Created: {Name} with {Steps} steps", name, steps.Count);
        return await Task.FromResult(chain.Id);
    }

    public async Task<ChainExecutionResult> ExecuteChainAsync(string chainId, Dictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        var chain = _chains.FirstOrDefault(c => c.Id == chainId);
        if (chain is null)
            return new ChainExecutionResult { Success = false, Error = $"Chain not found: {chainId}" };

        var executionId = Guid.NewGuid().ToString("N")[..8];
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningExecutions[executionId] = cts;

        var result = new ChainExecutionResult { ChainId = chainId, ExecutionId = executionId };
        var context = parameters ?? new Dictionary<string, string>();

        try
        {
            foreach (var step in chain.Steps)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    result.Error = "Cancelled";
                    break;
                }

                _logger.LogInformation("[ActionChain] Executing step: {Type} - {Value}", step.Type, step.Value);

                var stepResult = await ExecuteStepAsync(step, context, cts.Token);
                result.StepResults.Add(stepResult);

                if (!stepResult.Success && !step.ContinueOnError)
                {
                    result.Error = $"Step failed: {step.Type} - {stepResult.Error}";
                    break;
                }

                if (step.OutputVariable is not null && stepResult.Output is not null)
                    context[step.OutputVariable] = stepResult.Output;
            }

            result.Success = result.Error is null;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            _runningExecutions.Remove(executionId);
            cts.Dispose();
        }

        return result;
    }

    public async Task<IReadOnlyList<ChainDefinition>> GetChainsAsync(CancellationToken ct = default)
    {
        return await Task.FromResult(_chains.ToList());
    }

    public async Task DeleteChainAsync(string chainId, CancellationToken ct = default)
    {
        _chains.RemoveAll(c => c.Id == chainId);
        Save();
        await Task.CompletedTask;
    }

    public async Task StopChainAsync(string executionId, CancellationToken ct = default)
    {
        if (_runningExecutions.TryGetValue(executionId, out var cts))
        {
            await cts.CancelAsync();
            _logger.LogInformation("[ActionChain] Stopped execution: {Id}", executionId);
        }
    }

    private async Task<StepResult> ExecuteStepAsync(ChainStep step, Dictionary<string, string> context, CancellationToken ct)
    {
        var resolvedValue = ResolveVariables(step.Value, context);

        return step.Type.ToLowerInvariant() switch
        {
            "delay" => await ExecuteDelayAsync(resolvedValue, ct),
            "log" => ExecuteLog(resolvedValue),
            "set_variable" => ExecuteSetVariable(step, context),
            _ => new StepResult { Success = true, Output = $"Step type '{step.Type}' noted" }
        };
    }

    private async Task<StepResult> ExecuteDelayAsync(string value, CancellationToken ct)
    {
        if (int.TryParse(value, out var ms))
            await Task.Delay(ms, ct);
        return new StepResult { Success = true };
    }

    private StepResult ExecuteLog(string value)
    {
        _logger.LogInformation("[ActionChain] {Message}", value);
        return new StepResult { Success = true };
    }

    private StepResult ExecuteSetVariable(ChainStep step, Dictionary<string, string> context)
    {
        if (step.OutputVariable is not null)
            context[step.OutputVariable] = step.Value ?? "";
        return new StepResult { Success = true };
    }

    private static string ResolveVariables(string? template, Dictionary<string, string> context)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var result = template;
        foreach (var kv in context)
            result = result.Replace($"{{{kv.Key}}}", kv.Value);
        return result;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<List<ChainDefinition>>(json);
                if (loaded is not null) _chains.AddRange(loaded);
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
            var json = JsonSerializer.Serialize(_chains, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch { }
    }
}

public sealed class ChainDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ChainStep> Steps { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public sealed class ChainStep
{
    public string Type { get; set; } = "";
    public string? Value { get; set; }
    public string? OutputVariable { get; set; }
    public bool ContinueOnError { get; set; }
}

public sealed class ChainExecutionResult
{
    public string ChainId { get; set; } = "";
    public string ExecutionId { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<StepResult> StepResults { get; set; } = new();
}

public sealed class StepResult
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}
