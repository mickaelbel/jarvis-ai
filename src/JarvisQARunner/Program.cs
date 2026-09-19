using JarvisAI.Application.AI;
using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure;
using JarvisAI.Infrastructure.AI;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisQARunner;

public sealed class Program
{
    private static int _passed;
    private static int _failed;
    private static int _partial;
    private static readonly List<TestResult> Results = new();
    private static readonly string ReportDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "JarvisQA_Reports");

    public static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(ReportDir);

        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("  JARVIS QA RUNNER — Automated Use Case Testing");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(new ModelRouterOptions());
        services.AddSingleton(new AIOptions
        {
            HiddenPlanningEnabled = false,
            SelfVerificationEnabled = true,
            MaxToolRounds = 15,
            MaxAgentLoopSeconds = 120
        });
        services.AddInfrastructure();
        services.AddSingleton<IAutomaticMemoryService, NullAutomaticMemoryService>();

        await using var provider = services.BuildServiceProvider();

        var aiProvider = provider.GetRequiredService<IAIProvider>();

        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        foreach (var tool in provider.GetRequiredService<IEnumerable<ITool>>())
            registry.Register(tool);
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance);
        var memory = new SimpleMemoryService();
        var inner = new AIService(aiProvider, registry, executor, eventBus, memory, NullLogger<AIService>.Instance);
        var router = new ModelRouter(new ModelRouterOptions(), NullLogger<ModelRouter>.Instance);
        var options = new AIOptions
        {
            HiddenPlanningEnabled = false,
            SelfVerificationEnabled = true,
            MaxToolRounds = 15,
            MaxAgentLoopSeconds = 120
        };
        var adapter = new AIServiceAdapter(inner, aiProvider, router, registry, executor,
            NullLogger<AIServiceAdapter>.Instance, null!, options: options);

        var useCases = UseCaseGenerator.GenerateAll();
        Console.WriteLine($"Generated {useCases.Count} use cases across {useCases.GroupBy(u => u.Category).Count()} categories");
        Console.WriteLine();

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var semaphore = new SemaphoreSlim(3);

        var tasks = useCases.Select(async (uc, index) =>
        {
            await semaphore.WaitAsync(cts.Token);
            try { await RunUseCase(adapter, uc, index + 1, useCases.Count, cts.Token); }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);

        var report = GenerateReport(useCases, Results);
        var reportPath = Path.Combine(ReportDir, $"QA_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await File.WriteAllTextAsync(reportPath, report);
        Console.WriteLine();
        Console.WriteLine($"Report saved to: {reportPath}");
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine($"  RESULTS: {_passed} PASS / {_partial} PARTIAL / {_failed} FAIL / {useCases.Count} TOTAL");
        Console.WriteLine($"  SUCCESS RATE: {(useCases.Count > 0 ? (_passed * 100.0 / useCases.Count) : 0):F1}%");
        Console.WriteLine("═══════════════════════════════════════════════════");

        return _failed > 0 ? 1 : 0;
    }

    private static async Task RunUseCase(
        AIServiceAdapter adapter, UseCase uc, int current, int total, CancellationToken ct)
    {
        var label = $"[{current:D3}/{total}]";
        Console.Write($"{label} {uc.Id}: {Truncate(uc.Prompt, 50)}... ");

        var result = new TestResult { UseCase = uc, StartTime = DateTime.UtcNow };

        try
        {
            using var testCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            testCts.CancelAfter(TimeSpan.FromSeconds(uc.TimeoutSeconds));

            var tokens = new List<string>();
            var toolCallsMade = new List<string>();

            await foreach (var token in adapter.StreamChatAsync(
                uc.Prompt, cancellationToken: testCts.Token))
            {
                tokens.Add(token);
                if (token.Contains("✓") || token.Contains("computer_action") || token.Contains("browser"))
                    toolCallsMade.Add(token.Trim());
            }

            var response = string.Join("", tokens);
            result.Response = response;
            result.ToolCalls = toolCallsMade;
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            var verdict = uc.Evaluate(response, toolCallsMade);
            result.Verdict = verdict;

            switch (verdict)
            {
                case Verdict.Pass:
                    Interlocked.Increment(ref _passed);
                    Console.WriteLine($"✓ PASS ({result.Duration.TotalSeconds:F1}s)");
                    break;
                case Verdict.Partial:
                    Interlocked.Increment(ref _partial);
                    Console.WriteLine($"~ PARTIAL ({result.Duration.TotalSeconds:F1}s)");
                    break;
                case Verdict.Fail:
                    Interlocked.Increment(ref _failed);
                    Console.WriteLine($"✗ FAIL ({result.Duration.TotalSeconds:F1}s)");
                    Console.WriteLine($"    Response: {Truncate(response, 120)}");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            result.Verdict = Verdict.Fail;
            result.FailureReason = "Timeout";
            result.Duration = DateTime.UtcNow - result.StartTime;
            Interlocked.Increment(ref _failed);
            Console.WriteLine($"✗ FAIL (timeout {uc.TimeoutSeconds}s)");
        }
        catch (Exception ex)
        {
            result.Verdict = Verdict.Fail;
            result.FailureReason = $"Exception: {ex.Message}";
            result.Duration = DateTime.UtcNow - result.StartTime;
            Interlocked.Increment(ref _failed);
            Console.WriteLine($"✗ FAIL ({Truncate(ex.Message, 80)})");
        }

        lock (Results) { Results.Add(result); }
    }

    private static string GenerateReport(List<UseCase> useCases, List<TestResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine("  JARVIS QA REPORT");
        sb.AppendLine($"  Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine($"TOTAL: {useCases.Count}");
        sb.AppendLine($"PASS: {_passed}");
        sb.AppendLine($"PARTIAL: {_partial}");
        sb.AppendLine($"FAIL: {_failed}");
        sb.AppendLine($"SUCCESS RATE: {(useCases.Count > 0 ? (_passed * 100.0 / useCases.Count) : 0):F1}%");
        sb.AppendLine();
        sb.AppendLine("═══ BY CATEGORY ═══");
        foreach (var cat in useCases.GroupBy(u => u.Category).OrderBy(g => g.Key))
        {
            var catResults = results.Where(r => r.UseCase.Category == cat.Key).ToList();
            var catPass = catResults.Count(r => r.Verdict == Verdict.Pass);
            var catFail = catResults.Count(r => r.Verdict == Verdict.Fail);
            var catPartial = catResults.Count(r => r.Verdict == Verdict.Partial);
            sb.AppendLine($"  {cat.Key}: {catPass} PASS / {catPartial} PARTIAL / {catFail} FAIL ({cat.Count()} total)");
        }
        sb.AppendLine();
        sb.AppendLine("═══ BY DIFFICULTY ═══");
        foreach (var diff in useCases.GroupBy(u => u.Difficulty).OrderBy(g => g.Key))
        {
            var diffResults = results.Where(r => r.UseCase.Difficulty == diff.Key).ToList();
            var diffPass = diffResults.Count(r => r.Verdict == Verdict.Pass);
            sb.AppendLine($"  {diff.Key}: {diffPass}/{diff.Count()} PASS ({(diff.Count() > 0 ? diffPass * 100.0 / diff.Count() : 0):F0}%)");
        }
        sb.AppendLine();
        var failed = results.Where(r => r.Verdict == Verdict.Fail).ToList();
        if (failed.Any())
        {
            sb.AppendLine("═══ FAILED TESTS ═══");
            foreach (var f in failed)
            {
                sb.AppendLine($"  {f.UseCase.Id}: {f.UseCase.Prompt}");
                sb.AppendLine($"    Category: {f.UseCase.Category} | Difficulty: {f.UseCase.Difficulty}");
                sb.AppendLine($"    Expected: {f.UseCase.ExpectedBehavior}");
                sb.AppendLine($"    Got: {Truncate(f.Response ?? "(no response)", 200)}");
                sb.AppendLine($"    Failure: {f.FailureReason ?? "Response did not match expectations"}");
                sb.AppendLine($"    Duration: {f.Duration.TotalSeconds:F1}s");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}

internal sealed class SimpleMemoryService : IMemoryService
{
    private readonly List<MemoryEntry> _memories = new();

    public Task<MemoryEntry> SaveAsync(string key, string content, MemoryType type, string category,
        float importance = 0.5f, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new MemoryEntry { Key = key, Content = content, Category = category };
        _memories.Add(entry);
        return Task.FromResult(entry);
    }

    public Task<MemoryEntry> SaveMemoryAsync(string key, string content, MemoryType type, string category,
        float importance = 0.5f, MemoryTier tier = MemoryTier.LongTerm, string? project = null,
        TimeSpan? ttl = null, Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new MemoryEntry { Key = key, Content = content, Category = category };
        _memories.Add(entry);
        return Task.FromResult(entry);
    }

    public Task<MemoryEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_memories.FirstOrDefault(m => m.Key == key));
    }

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(_memories.Take(10).ToList());
    }

    public Task<IReadOnlyList<MemoryEntry>> SearchSemanticAsync(string query, int limit = 10,
        MemoryTier? tier = null, string? category = null, string? project = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(_memories.Take(limit).ToList());
    }

    public Task<MemoryContext> BuildContextAsync(string query, string? project = null,
        int limitPerScope = 6, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new MemoryContext());
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var removed = _memories.RemoveAll(m => m.Key == key);
        return Task.FromResult(removed > 0);
    }

    public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }

    public Task<IReadOnlyList<MemoryEntry>> GetContextAsync(string category, int maxEntries = 20,
        CancellationToken cancellationToken = default)
    {
        var entries = category is null
            ? _memories.Take(maxEntries).ToList()
            : _memories.Where(m => m.Category == category).Take(maxEntries).ToList();
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(entries);
    }
}

internal sealed class NullAutomaticMemoryService : IAutomaticMemoryService
{
    public bool IsDurable(string content, MemoryType type, string category) => false;
    public (MemoryTier Tier, TimeSpan? Ttl) DecideStorage(int importance, string content, MemoryType type, string category) => (MemoryTier.ShortTerm, null);
    public Task<bool> ConsiderSaveAsync(string content, string? goal, MemoryType type = MemoryType.Knowledge, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task SaveObservationAsync(string content, string? goal, MemoryType type, int importance, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public int ComputeImportance(string content) => 0;
    public MemoryType Categorize(string content) => MemoryType.Knowledge;
    public MemoryTier DecideTier(int importance) => MemoryTier.ShortTerm;
    public TimeSpan? DecideExpiration(int importance, MemoryTier tier) => null;
    public bool ShouldSave(string content, int importance) => false;
    public bool IsSavingEnabled() => false;
}
