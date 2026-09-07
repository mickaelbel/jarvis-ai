using JarvisAI.Application.Agents;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace JarvisAI.Tests;

public sealed class SecuritySystemTests
{
    [Fact]
    public void SecurityOptions_mode_safe_by_default()
    {
        var options = new SecurityOptions();
        Assert.NotNull(options);
        Assert.Equal(OperationMode.Safe, options.Mode);
    }

    [Fact]
    public void SecurityOptions_allows_user_profile_and_temp_by_default()
    {
        var options = new SecurityOptions();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.Contains(userProfile, options.AllowedPaths);
        Assert.Contains(options.AllowedPaths, p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(temp, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("C:\\Windows\\System32", options.AllowedPaths);
    }

    [Fact]
    public void SecurityManager_IsPathAllowed_accepts_allowed_directory()
    {
        var security = CreateSecurityManager();
        Assert.True(security.IsPathAllowed(Path.GetTempPath()));
    }

    [Fact]
    public void SecurityManager_IsPathAllowed_rejects_system32()
    {
        var security = CreateSecurityManager();
        Assert.False(security.IsPathAllowed(@"C:\Windows\System32"));
    }

    [Fact]
    public void SecurityManager_IsPathAllowed_rejects_null_or_empty()
    {
        var security = CreateSecurityManager();
        Assert.False(security.IsPathAllowed(null!));
        Assert.False(security.IsPathAllowed("  "));
    }

    [Fact]
    public void SecurityManager_IsPathAllowed_rejects_sibling_directory_with_same_prefix()
    {
        var security = CreateSecurityManager();
        var userProfile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        Assert.False(security.IsPathAllowed(userProfile + "2"));
        Assert.False(security.IsPathAllowed(userProfile + "_backup"));
        Assert.False(security.IsPathAllowed(userProfile + "\\..\\..\\Windows"));
    }

    [Fact]
    public void SecurityManager_IsPathAllowed_accepts_nested_and_rejects_overlapping_prefixes()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var confirmation = new MockConfirmationService();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var options = new SecurityOptions
        {
            AllowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Test\Root" }
        };
        var security = new SecurityManager(new Lazy<IToolRegistry>(() => registry), confirmation, eventBus, NullLogger<SecurityManager>.Instance, options);

        Assert.True(security.IsPathAllowed(@"C:\Test\Root"));
        Assert.True(security.IsPathAllowed(@"C:\Test\Root\sub\file.txt"));
        Assert.False(security.IsPathAllowed(@"C:\Test\Root2\file.txt"));
        Assert.False(security.IsPathAllowed(@"C:\Test\Rooted"));
        Assert.False(security.IsPathAllowed(@"C:\Test\Root\..\..\Windows"));
    }

    [Fact]
    public void SecurityOptions_disables_confirmation_bypass_by_default()
    {
        var options = new SecurityOptions();
        Assert.False(options.AllowDisableConfirmation);
    }

    [Fact]
    public void SecurityManager_IsCommandBlacklisted_rejects_dangerous_commands()
    {
        var security = CreateSecurityManager();
        Assert.True(security.IsCommandBlacklisted("shutdown /s"));
        Assert.True(security.IsCommandBlacklisted("format c:"));
        Assert.True(security.IsCommandBlacklisted("rm -rf C:\\Users"));
        Assert.True(security.IsCommandBlacklisted("diskpart"));
        Assert.True(security.IsCommandBlacklisted("reg delete HKLM\\Something"));
    }

    [Fact]
    public void SecurityManager_IsCommandBlacklisted_allows_safe_commands()
    {
        var security = CreateSecurityManager();
        Assert.False(security.IsCommandBlacklisted("dir"));
        Assert.False(security.IsCommandBlacklisted("echo hello"));
        Assert.False(security.IsCommandBlacklisted("git status"));
        Assert.False(security.IsCommandBlacklisted(null!));
    }

    [Fact]
    public async Task FileSystemTool_blocks_write_outside_allowed_paths()
    {
        var security = CreateSecurityManager();
        var tool = new FileSystemTool(NullLogger<FileSystemTool>.Instance, security);

        var result = await tool.ExecuteAsync(Context("test"), Args(("action", "write_file"), ("path", @"C:\Windows\System32\evil.txt"), ("content", "boom")));

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.ErrorMessage);
    }

    [Fact]
    public async Task FileSystemTool_blocks_delete_outside_allowed_paths()
    {
        var security = CreateSecurityManager();
        var tool = new FileSystemTool(NullLogger<FileSystemTool>.Instance, security);

        var result = await tool.ExecuteAsync(Context("test"), Args(("action", "delete_file"), ("path", @"C:\Windows\System32\evil.txt")));

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.ErrorMessage);
    }

    [Fact]
    public async Task FileSystemTool_allows_write_inside_temp()
    {
        var security = CreateSecurityManager();
        var tool = new FileSystemTool(NullLogger<FileSystemTool>.Instance, security);
        var target = Path.Combine(Path.GetTempPath(), "jarvis_security_test.txt");

        try
        {
            var result = await tool.ExecuteAsync(Context("test"), Args(("action", "write_file"), ("path", target), ("content", "hello")));
            Assert.True(result.Success);
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact]
    public async Task FileSystemTool_without_security_allows_any_path()
    {
        var tool = new FileSystemTool(NullLogger<FileSystemTool>.Instance);
        var target = Path.Combine(Path.GetTempPath(), "jarvis_security_test2.txt");

        try
        {
            var result = await tool.ExecuteAsync(Context("test"), Args(("action", "write_file"), ("path", target), ("content", "hello")));
            Assert.True(result.Success);
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact]
    public async Task TerminalTool_blocks_blacklisted_command()
    {
        var security = CreateSecurityManager();
        var tool = new TerminalTool(NullLogger<TerminalTool>.Instance, security);

        var result = await tool.ExecuteAsync(Context("test"), Args(("action", "execute_command"), ("command", "shutdown /s /t 0")));

        Assert.False(result.Success);
        Assert.Contains("Commande bloquée par la politique de sécurité", result.ErrorMessage);
    }

    [Fact]
    public async Task TerminalTool_blocks_working_directory_outside_allowed_paths()
    {
        var security = CreateSecurityManager();
        var tool = new TerminalTool(NullLogger<TerminalTool>.Instance, security);

        var result = await tool.ExecuteAsync(Context("test"), Args(("action", "execute_command"), ("command", "echo ok"), ("working_directory", @"C:\Windows\System32")));

        Assert.False(result.Success);
        Assert.Contains("hors des dossiers autorisés", result.ErrorMessage);
    }

    [Fact]
    public async Task TerminalTool_allows_safe_command_in_temp()
    {
        var security = CreateSecurityManager();
        var tool = new TerminalTool(NullLogger<TerminalTool>.Instance, security);

        var result = await tool.ExecuteAsync(Context("test"), Args(("action", "execute_command"), ("command", "echo ok"), ("working_directory", Path.GetTempPath())));

        Assert.True(result.Success);
    }

    [Fact]
    public void ToolTimeoutOptions_returns_default_and_per_tool_timeouts()
    {
        var options = new ToolTimeoutOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), options.GetTimeout("unknown_tool"));
        Assert.Equal(TimeSpan.FromSeconds(120), options.GetTimeout("terminal"));
        Assert.Equal(TimeSpan.FromSeconds(30), options.GetTimeout("computer_use"));
        Assert.Equal(TimeSpan.FromSeconds(60), options.GetTimeout("browser"));
    }

    [Fact]
    public async Task ToolExecutor_returns_timeout_error_when_tool_hangs()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new HangingTool());
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var timeoutOptions = new ToolTimeoutOptions { DefaultTimeout = TimeSpan.FromMilliseconds(200) };
        var executor = new ToolExecutor(registry, eventBus, NullLogger<ToolExecutor>.Instance, timeoutOptions: timeoutOptions);

        var context = new AgentContext("test", source: "test",
            metadata: new Dictionary<string, object> { ["arguments"] = new Dictionary<string, string>() });

        var result = await executor.ExecuteAsync("hanging_tool", context);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    [Fact]
    public void TaskExecutionHistory_records_steps_and_completion()
    {
        var history = new InMemoryTaskExecutionHistory();
        var correlationId = Guid.NewGuid();

        history.StartRecording("hello world", correlationId);
        history.AddStep(TaskExecutionStep.Thought("thinking"));
        history.AddStep(TaskExecutionStep.Tool("file_system", "write_file", 5, true));
        var record = history.Complete("done", true);

        Assert.Equal(correlationId, record.CorrelationId);
        Assert.Equal("hello world", record.UserMessage);
        Assert.Equal("done", record.FinalResponse);
        Assert.True(record.Success);
        Assert.Equal(1, record.ToolCallCount);
        Assert.Equal(2, record.Steps.Count);
        Assert.Same(record, history.GetHistory(correlationId));
    }

    private static SecurityManager CreateSecurityManager()
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var confirmation = new MockConfirmationService();
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new SecurityManager(new Lazy<IToolRegistry>(() => registry), confirmation, eventBus, NullLogger<SecurityManager>.Instance);
    }

    private static AgentContext Context(string commandText)
        => new(commandText, source: "test",
            metadata: new Dictionary<string, object> { ["arguments"] = new Dictionary<string, string>() });

    private static Dictionary<string, string> Args(params (string Key, string Value)[] args)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (key, value) in args)
            dict[key] = value;
        return dict;
    }

    private sealed class HangingTool : ITool
    {
        public string Name => "hanging_tool";
        public string Description => "Hangs forever for timeout testing";
        public string Category => "test";
        public SecurityRiskLevel RiskLevel => SecurityRiskLevel.Low;

        public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        public async Task<ToolResult> ExecuteAsync(AgentContext context, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return ToolResult.Succeeded("never");
        }
    }
}
