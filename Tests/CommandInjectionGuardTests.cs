using JarvisAI.Application.Agents;
using JarvisAI.Application.Security;
using JarvisAI.Application.Tools;
using JarvisAI.Infrastructure.Events;
using JarvisAI.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace JarvisAI.Tests;

public sealed class CommandInjectionGuardTests
{
    // ================================================================
    // PoC payloads from the security mission. No secondary command must
    // ever be executed.
    // ================================================================

    [Theory]
    [InlineData("& whoami &")]
    [InlineData("dir & whoami")]
    [InlineData("echo hi && whoami")]
    [InlineData("| whoami")]
    [InlineData("dir | findstr /i secret")]
    [InlineData("; whoami")]
    [InlineData("echo hi; whoami")]
    [InlineData("echo \"hello\" & whoami")]
    public void Validate_blocks_chaining_metacharacters(string command)
    {
        var result = CommandInjectionGuard.Validate(command);

        Assert.False(result.Allowed);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData("cmd /c echo hi & del c:\\windows")]
    [InlineData("> file.txt")]
    [InlineData("echo hello < input.txt")]
    [InlineData("powershell -Command $(whoami)")]
    [InlineData("` whoami `")]
    [InlineData("echo ^& whoami")]
    [InlineData("echo a && echo b")]
    [InlineData("whoami\nwhoami")]
    [InlineData("whoami\r\nwhoami")]
    public void Validate_blocks_redirection_substitution_and_line_breaks(string command)
    {
        var result = CommandInjectionGuard.Validate(command);

        Assert.False(result.Allowed);
    }

    [Theory]
    [InlineData("echo hello")]
    [InlineData("git status")]
    [InlineData("npm install")]
    [InlineData("dotnet build -c Release")]
    [InlineData("dir")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Validate_allows_plain_safe_commands(string? command)
    {
        var result = CommandInjectionGuard.Validate(command);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Validate_blocks_null_byte()
    {
        var result = CommandInjectionGuard.Validate("whoami\0 && dir");

        Assert.False(result.Allowed);
        Assert.Contains("Null byte", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_blocks_powershell_subexpression()
    {
        var result = CommandInjectionGuard.Validate("Write-Output $(Get-Process).Id");

        Assert.False(result.Allowed);
        Assert.Contains("sub-expression", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_enforces_whitelist_when_configured()
    {
        var options = new SecurityOptions
        {
            AllowedTerminalCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git", "npm" }
        };

        Assert.True(CommandInjectionGuard.Validate("git status", options).Allowed);
        Assert.True(CommandInjectionGuard.Validate("npm install", options).Allowed);
        Assert.False(CommandInjectionGuard.Validate("powershell Get-ChildItem", options).Allowed);
        Assert.False(CommandInjectionGuard.Validate("dotnet build", options).Allowed);
    }

    [Fact]
    public void Validate_empty_whitelist_does_not_restrict_commands()
    {
        var options = new SecurityOptions(); // AllowedTerminalCommands is empty by default

        Assert.True(CommandInjectionGuard.Validate("dotnet build", options).Allowed);
        Assert.True(CommandInjectionGuard.Validate("git status", options).Allowed);
    }

    [Theory]
    [InlineData("git status", "git")]
    [InlineData("npm install --save-dev xunit", "npm")]
    [InlineData("dotnet build -c Release", "dotnet")]
    [InlineData("\"C:\\Program Files\\App\\tool.exe\" --help", "tool")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractCommandName_parses_first_token(string? command, string? expected)
    {
        Assert.Equal(expected, CommandInjectionGuard.ExtractCommandName(command));
    }

    // ================================================================
    // End-to-end: the TerminalTool must refuse the payloads.
    // ================================================================

    [Fact]
    public async Task TerminalTool_rejects_injection_payloads_end_to_end()
    {
        var security = CreateSecurityManager();
        var tool = new TerminalTool(NullLogger<TerminalTool>.Instance, security);

        var payloads = new[]
        {
            "& whoami &",
            "| whoami",
            "; whoami",
            "echo hi & whoami",
            "echo \"hello\" | whoami",
            "whoami\nwhoami"
        };

        foreach (var payload in payloads)
        {
            var result = await tool.ExecuteAsync(
                new AgentContext("test"),
                new Dictionary<string, string>
                {
                    ["action"] = "execute_command",
                    ["command"] = payload
                });

            Assert.False(result.Success, $"Payload '{payload}' must be blocked");
            Assert.Contains("blocked by security policy", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TerminalTool_allows_safe_command_with_injection_guard_enabled()
    {
        var security = CreateSecurityManager();
        var tool = new TerminalTool(NullLogger<TerminalTool>.Instance, security);

        var result = await tool.ExecuteAsync(
            new AgentContext("test"),
            new Dictionary<string, string>
            {
                ["action"] = "execute_command",
                ["command"] = "echo ok",
                ["working_directory"] = Path.GetTempPath()
            });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task TerminalTool_injection_guard_can_be_disabled_via_options()
    {
        var options = new SecurityOptions { EnableCommandInjectionGuard = false };
        var security = CreateSecurityManager(options);
        var tool = new TerminalTool(NullLogger<TerminalTool>.Instance, security);

        // The blacklist still applies; only a blacklisted command is blocked.
        var blocked = await tool.ExecuteAsync(
            new AgentContext("test"),
            new Dictionary<string, string>
            {
                ["action"] = "execute_command",
                ["command"] = "shutdown /s /t 0"
            });

        Assert.False(blocked.Success);
    }

    [Fact]
    public async Task TerminalTool_enforces_whitelist_end_to_end()
    {
        var options = new SecurityOptions
        {
            AllowedTerminalCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "echo" }
        };
        var security = CreateSecurityManager(options);
        var tool = new TerminalTool(NullLogger<TerminalTool>.Instance, security);

        var allowed = await tool.ExecuteAsync(
            new AgentContext("test"),
            new Dictionary<string, string>
            {
                ["action"] = "execute_command",
                ["command"] = "echo ok",
                ["working_directory"] = Path.GetTempPath()
            });

        Assert.True(allowed.Success);

        var denied = await tool.ExecuteAsync(
            new AgentContext("test"),
            new Dictionary<string, string>
            {
                ["action"] = "execute_command",
                ["command"] = "dir"
            });

        Assert.False(denied.Success);
        Assert.Contains("whitelist", denied.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static SecurityManager CreateSecurityManager(SecurityOptions? options = null)
    {
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        return new SecurityManager(
            new Lazy<IToolRegistry>(() => new ToolRegistry(NullLogger<ToolRegistry>.Instance)),
            new MockConfirmationService(),
            eventBus,
            NullLogger<SecurityManager>.Instance,
            options);
    }
}
