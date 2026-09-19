using JarvisAI.Application.AI;
using JarvisAI.Application.Abstractions;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Budget;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Memory;
using JarvisAI.Application.Personality;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Application.Vision;
using JarvisAI.Application.Voice;
using JarvisAI.Application.WebAutomation;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure;
using JarvisAI.Infrastructure.AI;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Integrations;
using JarvisAI.Infrastructure.Reminders;
using JarvisAI.Infrastructure.Security;
using JarvisAI.Infrastructure.Tools;
using JarvisAI.Infrastructure.Voice;
using JarvisAI.Infrastructure.Windows;
using JarvisAI.Infrastructure.WebAutomation;
using JarvisAI.Infrastructure.ComputerUse;
using JarvisAI.Infrastructure.Vision;
using Microsoft.Extensions.DependencyInjection;
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

        // Parse --skip=ID1,ID2,... to skip known timeout/slow tests
        var skipIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            if (arg.StartsWith("--skip=", StringComparison.OrdinalIgnoreCase))
            {
                var ids = arg["--skip=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var id in ids) skipIds.Add(id.Trim());
            }
        }

        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("  JARVIS QA RUNNER — Automated Use Case Testing");
        Console.WriteLine("═══════════════════════════════════════════════════");
        if (headless)
            Console.WriteLine("  MODE: HEADLESS (no screen interaction)");
        if (skipIds.Count > 0)
            Console.WriteLine($"  Skipping {skipIds.Count} test(s): {string.Join(", ", skipIds)}");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));

        services.AddSingleton(new ModelRouterOptions());
        services.AddSingleton(new AIOptions
        {
            HiddenPlanningEnabled = false,
            SelfVerificationEnabled = true,
            MaxToolRounds = 15,
            MaxAgentLoopSeconds = 120
        });

        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<IntegrationsStore>();
        services.AddSingleton<IMemoryService, SimpleMemoryService>();
        services.AddSingleton<IAutomaticMemoryService, NullAutomaticMemoryService>();
        services.AddSingleton<IAuditLogService, NullAuditLogService>();
        services.AddSingleton<IErrorLearningService, NullErrorLearningService>();
        services.AddSingleton<ISecuritySandbox, NullSecuritySandbox>();
        services.AddSingleton<ISecurityManager>(sp => NullSecurityManager.Instance);
        services.AddSingleton<IUserConfirmationService, ConsoleConfirmationService>();
        services.AddSingleton<IPermissionStore, NullPermissionStore>();
        services.AddSingleton<SecurityOptions>();
        services.AddSingleton<SecurityPolicyStore>();
        services.AddSingleton(new ToolTimeoutOptions());
        services.AddSingleton<IBudgetTracker, NullBudgetTracker>();

        services.AddSingleton<OllamaRunMonitor>();
        services.AddSingleton<OllamaLauncher>();
        services.AddSingleton<OllamaProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<OllamaProvider>>();
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("http://localhost:11434"),
                Timeout = TimeSpan.FromMinutes(30)
            };
            var opts = sp.GetRequiredService<ModelRouterOptions>();
            return new OllamaProvider(httpClient, logger, model: "qwen3:8b",
                monitor: sp.GetRequiredService<OllamaRunMonitor>(),
                launcher: sp.GetRequiredService<OllamaLauncher>(),
                numCtx: opts.NumCtx);
        });
        services.AddSingleton<IAIProvider>(sp => sp.GetRequiredService<OllamaProvider>());
        services.AddSingleton<AiProviderSettingsStore>();
        services.AddSingleton<RoutingProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<RoutingProvider>>();
            var providers = new List<IAIProvider> { sp.GetRequiredService<OllamaProvider>() };
            return new RoutingProvider(providers, logger);
        });
        services.AddSingleton<AIService>();

        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IOcrService, NullOcrService>();
        services.AddSingleton<IVisionService, NullVisionService>();
        services.AddSingleton<IObservationProvider, ScreenAndPageObservationProvider>();

        // Headless mode: mock controller + vision, no real screen interaction
        HeadlessComputerController? headlessController = null;
        if (headless)
        {
            headlessController = new HeadlessComputerController();
            services.AddSingleton<IComputerController>(headlessController);
            services.AddSingleton<IComputerUseService>(new HeadlessComputerUseService(headlessController));
            services.AddSingleton<IUiElementDetector>(new HeadlessUiElementDetector());
        }
        else
        {
            services.AddSingleton<IComputerController, WindowsComputerController>();
            services.AddSingleton<IUiElementDetector, OcrUiElementDetector>();
            services.AddSingleton<IComputerUseService, ComputerUseService>();
        }
        services.AddSingleton<IWebBrowser, PlaywrightWebBrowser>();
        services.AddSingleton<BrowserManager>();
        services.AddSingleton<IDictationService, StubDictationService>();
        services.AddSingleton<IVoiceConfirmationChannel, StubVoiceConfirmationChannel>();
        services.AddSingleton<IVoiceSettingsStore, StubVoiceSettingsStore>();
        services.AddSingleton<ITextToSpeechService, StubTextToSpeechService>();
        services.AddSingleton<JarvisAI.Application.Services.IReminderService, JarvisAI.Application.Services.ReminderService>();
        services.AddSingleton<IDictationService, StubDictationService>();
        services.AddSingleton<IVoiceConfirmationChannel, StubVoiceConfirmationChannel>();
        services.AddSingleton<IVoiceSettingsStore, StubVoiceSettingsStore>();
        services.AddSingleton<ITextToSpeechService, StubTextToSpeechService>();

        services.AddSingleton<ITool, DateTimeTool>();
        services.AddSingleton<ITool, SystemInfoTool>();
        services.AddSingleton<ITool, CalculatorTool>();
        services.AddSingleton<ITool, MemoryTool>();
        services.AddSingleton<ITool, FileSystemTool>();
        services.AddSingleton<ITool, ReadDocumentTool>();
        services.AddSingleton<ITool, TerminalTool>();
        services.AddSingleton<ITool, ProcessTool>();
        services.AddSingleton<ITool, ClipboardTool>();
        services.AddSingleton<ITool, WindowsTool>();
        services.AddSingleton<ITool, WebPageTool>();
        services.AddSingleton<ITool, PowerTool>();
        services.AddSingleton<ITool, DictationTool>();
        services.AddSingleton<ITool>(sp => new ReminderTool(sp.GetRequiredService<JarvisAI.Application.Services.IReminderService>()));
        services.AddSingleton<ITool>(sp => new ModelChangeTool(
            sp.GetRequiredService<ModelOverrideStore>(),
            sp.GetRequiredService<ModelRouterOptions>()));

        services.AddSingleton<ITool, ComputerActionTool>();
        services.AddSingleton<ITool, BrowserTool>();
        services.AddSingleton<ITool, VisionTool>();

        services.AddSingleton<ModelOverrideStore>();
        services.AddSingleton<PersonalityStore>();

        services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry(sp.GetRequiredService<ILogger<ToolRegistry>>());
            registry.SetToolResolver(() => sp.GetServices<ITool>());
            return registry;
        });
        services.AddSingleton(sp => new Lazy<IToolRegistry>(sp.GetRequiredService<IToolRegistry>));

        await using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IToolRegistry>();
        var aiProvider = provider.GetRequiredService<IAIProvider>();

        Console.WriteLine($"Tools registered: {registry.GetAll().Count}");

        var eventBus = provider.GetRequiredService<IEventBus>();
        var executor = provider.GetRequiredService<IToolExecutor>();
        var memory = provider.GetRequiredService<IMemoryService>();

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

        // Preflight: check Ollama is reachable
        Console.Write("Preflight: checking Ollama... ");
        try
        {
            using var check = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await check.GetAsync("http://localhost:11434/api/tags");
            resp.EnsureSuccessStatusCode();
            Console.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.Message}");
            Console.WriteLine("Cannot reach Ollama at localhost:11434. Aborting.");
            return 1;
        }

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

        foreach (var (uc, index) in useCases.Select((uc, i) => (uc, i)))
        {
            if (cts.IsCancellationRequested) break;

            // Skip tests in the skip-list
            if (skipIds.Contains(uc.Id))
            {
                Console.WriteLine($"[{index + 1:D3}/{useCases.Count}] {uc.Id}: {Truncate(uc.Prompt, 50)}... ⏭ SKIP");
                continue;
            }

            try
            {
                await RunUseCase(adapter, uc, index + 1, useCases.Count, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout on a single test — continue to next test, don't abort
                Console.WriteLine($"[{index + 1:D3}/{useCases.Count}] {uc.Id}: ⏭ SKIP (outer timeout)");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"FATAL ERROR on {uc.Id}: {ex.Message}");
                Console.WriteLine("Aborting all remaining tests.");
                break;
            }
        }

        var report = GenerateReport(useCases, Results);
        var reportPath = Path.Combine(ReportDir, $"QA_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await File.WriteAllTextAsync(reportPath, report);
        Console.WriteLine();
        Console.WriteLine($"Report saved to: {reportPath}");

        CleanupTestFiles();

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

        // Snapshot PIDs BEFORE the test
        var pidsBefore = GetRunningPids();

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

        // Kill only NEW PIDs that appeared during the test (not pre-existing ones)
        KillNewPids(pidsBefore);

        lock (Results) { Results.Add(result); }
    }

    private static HashSet<int> GetRunningPids()
    {
        var pids = new HashSet<int>();
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try { pids.Add(p.Id); } catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return pids;
    }

    private static void KillNewPids(HashSet<int> pidsBefore)
    {
        try
        {
            // Snapshot names that existed BEFORE the test
            var namesBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pid in pidsBefore)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    namesBefore.Add(p.ProcessName);
                }
                catch { }
            }

            var pidsAfter = GetRunningPids();
            var newPids = pidsAfter.Except(pidsBefore).ToList();

            foreach (var pid in newPids)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    var name = p.ProcessName;

                    // If ANY process with this name existed before, it's a child of an existing app — skip
                    if (namesBefore.Contains(name)) continue;

                    // Never touch protected processes
                    if (IsProtectedProcess(name)) continue;

                    // Only kill apps that were NOT running before (Jarvis opened them)
                    p.Kill();
                    p.WaitForExit(1000);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsProtectedProcess(string name)
    {
        var n = name.ToLowerInvariant();

        // System / Windows
        if (n is "svchost" or "csrss" or "wininit" or "winlogon" or "lsass" or "services"
            or "smss" or "dwm" or "conhost" or "fontdrvhost" or "dllhost" or "wmiprvse"
            or "sihost" or "taskhostw" or "ctfmon" or "spoolsv" or "wlanext" or "dashost"
            or "searchindexer" or "searchapp" or "searchhost" or "memory compression"
            or "registry" or "idle" or "system" or "smartscreen"
            or "shellExperienceHost" or "startMenuExperienceHost"
            or "textinputhost" or "comppkgsrv" or "runtimebroker"
            or "securityhealthservice" or "securityhealthsystray"
            or "mcmdrun" or "msmpeng" or "gamebar" or "gamebarpresencewriter")
            return true;

        // Ollama / AI
        if (n is "ollama" or "llama-server" or "ollama_llama_server")
            return true;

        // Browsers — NEVER kill
        if (n is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi")
            return true;

        // .NET / Dev tools
        if (n is "dotnet" or "devenv" or "code" or "jetbrains" or "rider64" or "resharper")
            return true;

        // User apps — NEVER kill
        if (n is "explorer" or "spotify" or "discord" or "slack" or "teams"
            or "zoom" or "steam" or "epicgameslauncher" or "obs64" or "obs")
            return true;

        return false;
    }

    private static void CleanupTestFiles()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var testFiles = new[]
            {
                "bonjour.txt", "note.txt", "hello_world.txt", "test_jarvis.txt",
                "test_jarvis.csv", "test_jarvis.json", "test_jarvis.xml",
                "test_jarvis.py", "test_jarvis.sh", "test_jarvis.yaml",
                "test_jarvis.md", "test_jarvis_100.txt", "test_jarvis_log.txt",
                "hello_world.py", "test.py", "data.csv", "config.xml",
                "test.txt", "test.json", "test.md", "test.yaml"
            };
            foreach (var f in testFiles)
            {
                var path = Path.Combine(desktop, f);
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            }

            // Also clean test folders on Desktop
            var testFolders = new[] { "test_jarvis", "jarvis_test" };
            foreach (var d in testFolders)
            {
                var path = Path.Combine(desktop, d);
                if (Directory.Exists(path)) try { Directory.Delete(path, true); } catch { }
            }

            // Clean temp test files
            var tempDir = Path.GetTempPath();
            foreach (var f in Directory.GetFiles(tempDir, "jarvis_*"))
            {
                try { File.Delete(f); } catch { }
            }
            foreach (var d in Directory.GetDirectories(tempDir, "jarvis_*"))
            {
                try { Directory.Delete(d, true); } catch { }
            }

            Console.WriteLine("Test files cleaned up.");
        }
        catch { }
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

// ═══════════════ NULL/STUB IMPLEMENTATIONS ═══════════════

internal sealed class SimpleMemoryService : IMemoryService
{
    private readonly List<MemoryEntry> _memories = new();
    public Task<MemoryEntry> SaveAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken ct = default) { var e = new MemoryEntry { Key = key, Content = content, Category = category }; _memories.Add(e); return Task.FromResult(e); }
    public Task<MemoryEntry> SaveMemoryAsync(string key, string content, MemoryType type, string category, float importance = 0.5f, MemoryTier tier = MemoryTier.LongTerm, string? project = null, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null, CancellationToken ct = default) { var e = new MemoryEntry { Key = key, Content = content, Category = category }; _memories.Add(e); return Task.FromResult(e); }
    public Task<MemoryEntry?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_memories.FirstOrDefault(m => m.Key == key));
    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryEntry>>(_memories.Take(10).ToList());
    public Task<IReadOnlyList<MemoryEntry>> SearchSemanticAsync(string query, int limit = 10, MemoryTier? tier = null, string? category = null, string? project = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryEntry>>(_memories.Take(limit).ToList());
    public Task<MemoryContext> BuildContextAsync(string query, string? project = null, int limitPerScope = 6, CancellationToken ct = default) => Task.FromResult(new MemoryContext());
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(_memories.RemoveAll(m => m.Key == key) > 0);
    public Task<int> CleanupExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<MemoryEntry>> GetContextAsync(string category, int maxEntries = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryEntry>>(category is null ? _memories.Take(maxEntries).ToList() : _memories.Where(m => m.Category == category).Take(maxEntries).ToList());
}

internal sealed class NullAutomaticMemoryService : IAutomaticMemoryService
{
    public bool IsDurable(string content, MemoryType type, string category) => false;
    public (MemoryTier Tier, TimeSpan? Ttl) DecideStorage(int importance, string content, MemoryType type, string category) => (MemoryTier.ShortTerm, null);
    public Task<bool> ConsiderSaveAsync(string content, string? goal, MemoryType type = MemoryType.Knowledge, CancellationToken ct = default) => Task.FromResult(false);
    public Task SaveObservationAsync(string content, string? goal, MemoryType type, int importance, CancellationToken ct = default) => Task.CompletedTask;
    public int ComputeImportance(string content) => 0;
    public MemoryType Categorize(string content) => MemoryType.Knowledge;
    public MemoryTier DecideTier(int importance) => MemoryTier.ShortTerm;
    public TimeSpan? DecideExpiration(int importance, MemoryTier tier) => null;
    public bool ShouldSave(string content, int importance) => false;
    public bool IsSavingEnabled() => false;
}

internal sealed class NullAuditLogService : IAuditLogService
{
    public Task LogAsync(AuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int count = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());
    public Task<IReadOnlyList<AuditEntry>> GetByToolAsync(string toolName, int count = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());
    public Task<IReadOnlyList<AuditEntry>> GetErrorsAsync(int count = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());
    public Task<AuditStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new AuditStats(0, 0, 0, new(), new(), 0));
}

internal sealed class NullErrorLearningService : IErrorLearningService
{
    public Task RecordErrorAsync(string toolName, string action, string error, string? context = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> HasSeenErrorAsync(string toolName, string action, string error, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyList<ErrorRecord>> GetSimilarErrorsAsync(string toolName, string action, int count = 10, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ErrorRecord>>(Array.Empty<ErrorRecord>());
    public Task<IReadOnlyList<ErrorRecord>> GetRecentErrorsAsync(int count = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ErrorRecord>>(Array.Empty<ErrorRecord>());
    public Task<string?> GetSuggestionAsync(string toolName, string action, string error, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

internal sealed class NullSecuritySandbox : ISecuritySandbox
{
    public Task<SandboxResult> ExecuteInSandboxAsync(string command, SandboxOptions options, CancellationToken ct = default)
        => Task.FromResult(new SandboxResult(true, "", "", 0, 0, false));
    public bool IsDangerousCommand(string command) => false;
    public SandboxLevel GetRequiredLevel(string command) => SandboxLevel.Normal;
}

internal sealed class NullSecurityManager : ISecurityManager
{
    public static readonly NullSecurityManager Instance = new();
    public Task<bool> IsToolAllowedAsync(string toolName, Guid correlationId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<ConfirmationResult> CheckAndConfirmAsync(string toolName, AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
        => Task.FromResult(ConfirmationResult.AutoConfirmed());
    public bool IsCommandBlacklisted(string command) => false;
    public bool IsToolWhitelisted(string toolName) => true;
    public bool IsPathAllowed(string path) => true;
    public SecurityRiskLevel GetToolRiskLevel(string toolName) => SecurityRiskLevel.Safe;
    public void Configure(SecurityOptions options) { }
    public SecurityOptions GetOptions() => new();
}

internal sealed class NullPermissionStore : IPermissionStore
{
    public Task<bool> IsAlwaysAllowedAsync(string toolName, string? action = null, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> AllowAlwaysAsync(string toolName, string? action = null, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> RevokeAsync(string toolName, string? action = null, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

internal sealed class NullBudgetTracker : IBudgetTracker
{
    public Task RecordAsync(string model, int promptTokens, int completionTokens, CancellationToken ct = default) => Task.CompletedTask;
    public Task<BudgetState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(new BudgetState(0, 0, null, null, false, false, false));
    public string FormatSummary() => "";
    public string ApplyFallback(string requestedModel) => requestedModel;
}

internal sealed class NullOcrService : IOcrService
{
    public bool OcrAvailable => false;
    public Task<OcrResult?> ExtractTextAsync(byte[] imageBytes, string language = "fra+eng", CancellationToken ct = default) => Task.FromResult<OcrResult?>(null);
}

internal sealed class NullVisionService : IVisionService
{
    public Task<ImageDescription> DescribeImageAsync(byte[] imageBytes, string? prompt = null, CancellationToken ct = default) => Task.FromResult(new ImageDescription("", false, "Vision not available in QA runner"));
    public Task<ImageDescription> DescribeImageWithModelAsync(byte[] imageBytes, string model, string? prompt = null, CancellationToken ct = default) => Task.FromResult(new ImageDescription("", false, "Vision not available in QA runner"));
    public Task<MultiImageAnalysis> DescribeMultipleImagesAsync(IReadOnlyList<byte[]> images, string? summaryPrompt = null, CancellationToken ct = default) => Task.FromResult(new MultiImageAnalysis("", Array.Empty<string>(), false, "Vision not available"));
    public Task<VideoAnalysis> AnalyzeVideoAsync(string videoPath, string? prompt = null, int maxFrames = 8, CancellationToken ct = default) => Task.FromResult(new VideoAnalysis("", Array.Empty<string>(), 0, false, "Vision not available"));
    public Task<LocalizedElementsResult> LocalizeAsync(string label, byte[] imageBytes, CancellationToken ct = default) => Task.FromResult(new LocalizedElementsResult(Array.Empty<VisionElement>(), false, "Vision not available"));
    public Task<string> ResolveModelAsync(CancellationToken ct = default) => Task.FromResult("");
    public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
    public Task<bool> EnsureVisionModelAsync(CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class StubDictationService : IDictationService
{
    public bool IsEnabled => false;
    public void Activate() { }
    public void Deactivate() { }
}

internal sealed class StubVoiceConfirmationChannel : IVoiceConfirmationChannel
{
    public bool IsSupported => false;
    public Task<VoiceConfirmationAnswer?> AskAsync(string question, TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult<VoiceConfirmationAnswer?>(null);
}

internal sealed class StubVoiceSettingsStore : IVoiceSettingsStore
{
    private VoiceSettings _settings = new();
    public VoiceSettings Get() => _settings;
    public void Save(VoiceSettings settings) => _settings = settings;
}

internal sealed class StubTextToSpeechService : ITextToSpeechService
{
    public string Name => "stub";
    public IReadOnlyList<string> AvailableVoices => Array.Empty<string>();
    public Task<byte[]> SynthesizeWavAsync(string text, string voice, float volume = 1.0f, float speed = 1.0f, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
}
