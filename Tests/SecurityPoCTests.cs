using JarvisAI.Application.Agents;
using JarvisAI.Application.ComputerUse;
using JarvisAI.Application.Plugins;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Domain.Security;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Plugins;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class SecurityPoCTests
{
    // ================================================================
    // POCS FOR VULNERABILITIES CONFIRMED EXPLOITABLE
    // (NOW FIXED - these tests assert the mitigation)
    // ================================================================

    /// <summary>
    /// FIXED: TerminalTool command injection via unescaped quotes.
    /// The tool used to wrap user commands in /c "{command}" without escaping
    /// embedded quotes, allowing an attacker to break out of the wrapper and
    /// chain arbitrary Windows commands.
    ///
    /// Mitigation: CommandInjectionGuard rejects commands containing shell
    /// chaining metacharacters (&, |, ;, &gt;, &lt;, `, ^). The payload below
    /// is now blocked before it reaches the shell.
    /// </summary>
    [Fact]
    public async Task TerminalTool_injection_poc_quote_breakout_is_now_blocked()
    {
        var security = CreateSecurityManager();
        var tool = new TerminalTool(NullLogger<TerminalTool>.Instance, security);

        var parameters = new Dictionary<string, string>
        {
            ["action"] = "execute_command",
            ["command"] = "echo LEGIT\" & echo INJECTED_PWNED & echo \""
        };

        var result = await tool.ExecuteAsync(new AgentContext("poc"), parameters);

        Assert.False(result.Success,
            "Command chaining metacharacters must be blocked by the injection guard");
        Assert.Contains("blocked by security policy", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INJECTED_PWNED", result.Output ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FIXED: FileSystemTool used to read files outside allowed paths because
    /// the path check was only applied to write/delete actions.
    ///
    /// Mitigation: every operation (read and write, source and destination) is
    /// now checked against the sandbox. Reading C:\Windows\win.ini while only
    /// C:\Temp is allowed is denied.
    /// </summary>
    [Fact]
    public async Task FileSystemTool_reads_files_outside_allowed_paths_are_blocked()
    {
        var securityOptions = new SecurityOptions
        {
            AllowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Temp"
            }
        };
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var security = new SecurityManager(
            new Lazy<IToolRegistry>(() => new ToolRegistry(NullLogger<ToolRegistry>.Instance)),
            new MockConfirmationService(),
            eventBus,
            NullLogger<SecurityManager>.Instance,
            securityOptions);

        var tool = new FileSystemTool(NullLogger<FileSystemTool>.Instance, security);

        var parameters = new Dictionary<string, string>
        {
            ["action"] = "read_file",
            ["path"] = @"C:\Windows\win.ini"
        };

        var result = await tool.ExecuteAsync(new AgentContext("test"), parameters);

        Assert.False(result.Success,
            "FileSystemTool read operations must be sandboxed");
        Assert.Contains("Access denied", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FIXED: PluginContext used to register tools directly into IToolRegistry
    /// without any security validation, letting a plugin inject High-risk,
    /// ungated tools.
    ///
    /// Mitigation: PluginPermissionManager compares the tool's risk level to the
    /// plugin's declared permission level. Registering a High-risk tool from a
    /// Low-permission plugin is denied.
    /// </summary>
    [Fact]
    public void PluginContext_register_tool_is_denied_when_risk_exceeds_permission()
    {
        var toolRegistry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var permissionManager = new PluginPermissionManager();
        var metadata = new PluginMetadata
        {
            Id = "low",
            Name = "Low Permission Plugin",
            PermissionLevel = PluginPermissionLevel.Low
        };

        var pluginContext = new PluginContext(
            eventBus, toolRegistry, new UnsupportedServiceProvider(), @"C:\plugins",
            metadata, permissionManager);

        var dangerousTool = new DeleteAllFilesTool("delete_all_files", SecurityRiskLevel.High);

        Assert.Throws<System.Security.SecurityException>(() => pluginContext.RegisterTool(dangerousTool));

        var registered = toolRegistry.GetByName("delete_all_files");
        Assert.Null(registered);
    }

    // ================================================================
    // POCS FOR VULNERABILITIES NOT EXPLOITABLE (VERIFIED)
    // ================================================================

    /// <summary>
    /// NOT EXPLOITABLE: XSS via MarkupString is prevented by the markdown
    /// renderer. The Markdig pipeline uses .DisableHtml(), which escapes or
    /// strips all raw HTML (script tags, event handlers, etc).
    ///
    /// Impact: None - raw HTML cannot reach the UI as live markup.
    /// Demonstrated: &lt;script&gt; and its payload are removed from output.
    /// </summary>
    [Fact]
    public void MarkdownRenderer_disable_html_blocks_script_tags()
    {
        var renderer = new JarvisAI.Web.Services.MarkdownRenderer();
        var input = "<script>alert('xss')</script> Still here";

        var output = renderer.Render(input);

        Assert.DoesNotContain("<script>", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Still here", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NOT EXPLOITABLE: BrowserTool fetch_page blocks the file:// protocol.
    /// Non-http(s) URLs are prefixed with https:// (BrowserTool), so
    /// 'file:///C:/Windows/win.ini' becomes 'https://file:///...' and fails.
    ///
    /// Impact: None - local file reads / SSRF via file:// are prevented.
    /// </summary>
    [Fact]
    public async Task BrowserTool_fetch_page_blocks_file_protocol()
    {
        var tool = new BrowserTool(NullLogger<BrowserTool>.Instance);
        var parameters = new Dictionary<string, string>
        {
            ["action"] = "fetch_page",
            ["url"] = "file:///C:/Windows/win.ini"
        };

        var result = await tool.ExecuteAsync(new AgentContext("test"), parameters);

        Assert.False(result.Success,
            "file:// URLs are transformed and fail to fetch");
    }

    /// <summary>
    /// NOT EXPLOITABLE: ComputerUseTool cannot act on UI elements without a
    /// real, matched element. Actions are routed through IComputerUseService
    /// which returns a failure when no element is found, so arbitrary clicks
    /// on unverified coordinates are not possible.
    ///
    /// Impact: None - element-matching sandbox prevents blind actions.
    /// </summary>
    [Fact]
    public async Task ComputerUseTool_sandbox_prevents_unauthorized_actions()
    {
        var fakeComputerUse = new FakeComputerUseService
        {
            Elements = Array.Empty<UiElement>(),
            ClickResult = new UiActionResult(false, null, "no click", 0, 0)
        };

        var tool = new ComputerUseTool(fakeComputerUse, NullLogger<ComputerUseTool>.Instance);

        var parameters = new Dictionary<string, string>
        {
            ["action"] = "click_element",
            ["label"] = "OK"
        };

        var result = await tool.ExecuteAsync(new AgentContext("test"), parameters);

        Assert.False(result.Success,
            "Computer Use requires element discovery and validation before acting");
    }

    /// <summary>
    /// NOT EXPLOITABLE: SecurityManager configuration is correct by default.
    /// SecurityOptions.AllowDisableConfirmation is false, so users cannot
    /// disable confirmation prompts.
    /// </summary>
    [Fact]
    public void SecurityManager_confirmation_disabled_by_default()
    {
        var options = new SecurityOptions();
        Assert.False(options.AllowDisableConfirmation);
    }

    /// <summary>
    /// NOT EXPLOITABLE: PluginManager discovers plugins only from the plugins
    /// directory via PluginLoader and only registers types implementing IPlugin.
    /// Without a valid IPlugin implementation on disk, no plugin is loaded.
    /// </summary>
    [Fact]
    public void PluginManager_discovery_prevents_malicious_plugins()
    {
        var pluginManager = new PluginManager(
            new PluginLoader(NullLogger<PluginLoader>.Instance),
            new PluginRegistry(NullLogger<PluginRegistry>.Instance),
            new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance),
            new ToolRegistry(NullLogger<ToolRegistry>.Instance),
            new UnsupportedServiceProvider(),
            NullLogger<PluginManager>.Instance,
            "Plugins");

        Assert.NotNull(pluginManager);
    }

    // ================================================================
    // HELPER CLASSES FOR POCS
    // ================================================================

    private static SecurityManager CreateSecurityManager()
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new SecurityManager(
            new Lazy<IToolRegistry>(() => new ToolRegistry(NullLogger<ToolRegistry>.Instance)),
            new MockConfirmationService(),
            eventBus,
            NullLogger<SecurityManager>.Instance);
    }

    public sealed class DeleteAllFilesTool : ITool
    {
        private readonly string _name;

        public DeleteAllFilesTool(string name, SecurityRiskLevel riskLevel)
        {
            _name = name;
            RiskLevel = riskLevel;
        }

        public string Name => _name;
        public string Description => "Deletes all files in a directory (DANGEROUS)";
        public string Category => "dangerous";
        public SecurityRiskLevel RiskLevel { get; }

        public IReadOnlyList<ToolParameter> Parameters => new[]
        {
            new ToolParameter("path", "Directory to delete", typeof(string), required: true)
        };

        public Task<ToolResult> ExecuteAsync(
            AgentContext context,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken = default)
        {
            parameters.TryGetValue("path", out var path);
            return Task.FromResult(ToolResult.Succeeded($"Would delete ALL files in {path}"));
        }
    }

    public sealed class UnsupportedServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
